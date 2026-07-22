using System.Collections.Concurrent;
using System.Net;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;

namespace AirDrop.Diagnostics;

/// <summary>
/// Herramienta de diagnóstico del descubrimiento mDNS.
/// </summary>
/// <remarks>
/// <para>
/// Escucha la red local y lista qué servicios se anuncian, destacando los de Apple. Existe para
/// responder empíricamente a las preguntas que la investigación dejó abiertas (ver
/// docs/01-RESEARCH-airdrop-protocol.md §9), en particular el test 3: <b>¿anuncia algún
/// dispositivo Apple <c>_airdrop._tcp</c> sobre la Wi-Fi de infraestructura?</b>
/// </para>
/// <para>
/// La respuesta esperada, según la investigación, es que no: los dispositivos iOS solo anuncian
/// AirDrop sobre la interfaz AWDL. Esta herramienta permite confirmarlo con datos propios en vez
/// de darlo por bueno.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Meta-consulta de DNS-SD que pide a todo el mundo que declare sus tipos de servicio.</summary>
    private const string ServiceEnumerationQuery = "_services._dns-sd._udp.local";

    /// <summary>Servicios que delatan la presencia de un dispositivo Apple.</summary>
    private static readonly Dictionary<string, string> AppleServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_airdrop._tcp"] = "AirDrop  <-- CLAVE PARA ESTE PROYECTO",
        ["_companion-link._tcp"] = "Continuity / Handoff",
        ["_rdlink._tcp"] = "Remote Desktop / Continuity",
        ["_airplay._tcp"] = "AirPlay",
        ["_raop._tcp"] = "AirPlay Audio",
        ["_homekit._tcp"] = "HomeKit",
        ["_hap._tcp"] = "HomeKit Accessory Protocol",
        ["_sleep-proxy._udp"] = "Bonjour Sleep Proxy",
        ["_apple-mobdev2._tcp"] = "Emparejamiento de dispositivo iOS",
        ["_touch-able._tcp"] = "Apple TV Remote",
        ["_appletv-v2._tcp"] = "Apple TV",
    };

    private static async Task<int> Main(string[] args)
    {
        var duration = TimeSpan.FromSeconds(
            args.Length > 0 && int.TryParse(args[0], out var seconds) ? seconds : 15);

        Console.WriteLine("Diagnóstico de descubrimiento mDNS");
        Console.WriteLine("=================================");
        Console.WriteLine($"Escuchando durante {duration.TotalSeconds:F0} segundos...");
        Console.WriteLine();

        var serviceTypes = new ConcurrentDictionary<string, byte>();
        var instances = new ConcurrentDictionary<string, InstanceInfo>();

        await using var transport = new UdpMdnsTransport();

        try
        {
            await transport.StartAsync();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"No se pudo iniciar el transporte mDNS: {ex.Message}");
            return 1;
        }

        transport.PacketReceived += packet => Observe(packet, serviceTypes, instances);

        await ProbeAsync(transport, duration);

        Report(serviceTypes, instances);
        return 0;
    }

    /// <summary>Emite consultas periódicas para provocar respuestas en vez de solo escuchar pasivamente.</summary>
    private static async Task ProbeAsync(IMdnsTransport transport, TimeSpan duration)
    {
        var enumeration = DnsMessageWriter.Write(DnsMessage.CreateQuery(
            new DnsQuestion(ServiceEnumerationQuery, DnsRecordType.Ptr)));

        var airDrop = DnsMessageWriter.Write(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await transport.SendAsync(enumeration);
            await transport.SendAsync(airDrop);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static void Observe(
        MdnsPacket packet,
        ConcurrentDictionary<string, byte> serviceTypes,
        ConcurrentDictionary<string, InstanceInfo> instances)
    {
        DnsMessage message;
        try
        {
            message = DnsMessageReader.Read(packet.Payload);
        }
        catch (DnsFormatException)
        {
            return;
        }

        foreach (var record in message.Answers.Concat(message.Additionals))
        {
            switch (record)
            {
                case PtrRecord ptr when ptr.Name.StartsWith("_services.", StringComparison.OrdinalIgnoreCase):
                    // Respuesta a la meta-consulta: el destino ES un tipo de servicio.
                    serviceTypes.TryAdd(StripLocal(ptr.Target), 0);
                    break;

                case PtrRecord ptr:
                    serviceTypes.TryAdd(StripLocal(ptr.Name), 0);
                    instances.AddOrUpdate(
                        ptr.Target,
                        _ => new InstanceInfo(ptr.Target, packet.Source.Address),
                        (_, existing) => existing);
                    break;

                case SrvRecord srv:
                    instances.AddOrUpdate(
                        srv.Name,
                        _ => new InstanceInfo(srv.Name, packet.Source.Address)
                        {
                            Host = srv.Target,
                            Port = srv.Port,
                        },
                        (_, existing) =>
                        {
                            existing.Host = srv.Target;
                            existing.Port = srv.Port;
                            return existing;
                        });
                    break;

                case TxtRecord txt:
                    instances.AddOrUpdate(
                        txt.Name,
                        _ => new InstanceInfo(txt.Name, packet.Source.Address) { Txt = txt.ToPairs() },
                        (_, existing) =>
                        {
                            existing.Txt = txt.ToPairs();
                            return existing;
                        });
                    break;
            }
        }
    }

    private static void Report(
        ConcurrentDictionary<string, byte> serviceTypes,
        ConcurrentDictionary<string, InstanceInfo> instances)
    {
        Console.WriteLine();
        Console.WriteLine($"Tipos de servicio detectados: {serviceTypes.Count}");
        Console.WriteLine("--------------------------------------------------");

        foreach (var type in serviceTypes.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var apple = AppleServices.TryGetValue(type, out var description);
            Console.WriteLine(apple ? $"  {type,-28} {description}" : $"  {type}");
        }

        Console.WriteLine();
        Console.WriteLine($"Instancias detectadas: {instances.Count}");
        Console.WriteLine("--------------------------------------------------");

        foreach (var instance in instances.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {instance.Name}");
            Console.WriteLine($"      visto desde : {instance.SeenFrom}");

            if (instance.Host is not null)
            {
                Console.WriteLine($"      host:puerto : {instance.Host}:{instance.Port}");
            }

            if (instance.Txt is { Count: > 0 })
            {
                Console.WriteLine($"      TXT         : {string.Join(", ",
                    instance.Txt.Select(p => $"{p.Key}={p.Value}"))}");
            }
        }

        ReportConclusion(serviceTypes, instances);
    }

    /// <summary>Traduce los hallazgos a la pregunta que de verdad importa para este proyecto.</summary>
    private static void ReportConclusion(
        ConcurrentDictionary<string, byte> serviceTypes,
        ConcurrentDictionary<string, InstanceInfo> instances)
    {
        Console.WriteLine();
        Console.WriteLine("Conclusión");
        Console.WriteLine("==================================================");

        var appleFound = serviceTypes.Keys.Where(AppleServices.ContainsKey).ToList();
        var airDropInstances = instances.Values
            .Where(i => i.Name.Contains("_airdrop._tcp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (appleFound.Count == 0)
        {
            Console.WriteLine("  No se ha detectado ningún dispositivo Apple en esta red.");
            Console.WriteLine("  El test no es concluyente: acerca un iPhone o un Mac a la misma");
            Console.WriteLine("  red Wi-Fi y vuelve a ejecutarlo.");
            return;
        }

        Console.WriteLine($"  Dispositivos Apple presentes: sí ({appleFound.Count} servicios).");

        if (airDropInstances.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  *** Se ha detectado _airdrop._tcp sobre Wi-Fi de INFRAESTRUCTURA ***");
            Console.WriteLine("  Esto contradice la hipótesis de la investigación y merece");
            Console.WriteLine("  analizarse: podría abrir una vía sin AWDL.");

            foreach (var instance in airDropInstances)
            {
                Console.WriteLine($"    - {instance.Name} en {instance.Host}:{instance.Port}");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  No se anuncia _airdrop._tcp por la red de infraestructura, pese a");
            Console.WriteLine("  haber dispositivos Apple presentes.");
            Console.WriteLine("  Esto CONFIRMA la conclusión de la investigación: AirDrop solo se");
            Console.WriteLine("  anuncia sobre AWDL. Ver docs/01-RESEARCH-airdrop-protocol.md §7.");
        }
    }

    private static string StripLocal(string name) =>
        name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    private sealed record InstanceInfo(string Name, IPAddress SeenFrom)
    {
        public string? Host { get; set; }

        public ushort Port { get; set; }

        public IReadOnlyDictionary<string, string>? Txt { get; set; }
    }
}
