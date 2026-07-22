using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;

namespace AirDrop.MdnsProbe;

/// <summary>
/// Analiza en detalle el tráfico mDNS relacionado con AirDrop.
/// </summary>
/// <remarks>
/// <para>
/// Se escribió para investigar un hallazgo inesperado: durante la prueba de Bluetooth se
/// observaron <b>consultas</b> de <c>_airdrop._tcp</c> llegando por la Wi-Fi de infraestructura,
/// cuando la investigación concluía que AirDrop solo se anuncia sobre AWDL.
/// </para>
/// <para>
/// Vuelca cada paquete relevante con todo su detalle —quién lo envía, qué pregunta exactamente,
/// con qué banderas— para poder distinguir tres cosas que se parecen mucho desde fuera:
/// un iPhone buscando AirDrop de verdad, un Mac en modo "buscar Macs antiguos", y nuestro propio
/// tráfico rebotando.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
        var respond = args.Contains("--responder");

        Console.WriteLine("Análisis del tráfico mDNS de AirDrop");
        Console.WriteLine("====================================");
        Console.WriteLine();

        var localAddresses = GetLocalAddresses();
        Console.WriteLine("Direcciones de este equipo (para descartar el eco de uno mismo):");
        foreach (var address in localAddresses)
        {
            Console.WriteLine($"  {address}");
        }

        Console.WriteLine();

        if (respond)
        {
            Console.WriteLine("MODO RESPONDER ACTIVO: se contestará a las consultas de AirDrop.");
            Console.WriteLine();
        }

        Console.WriteLine($"Escuchando {seconds} segundos. Abre Compartir -> AirDrop en el iPhone.");
        Console.WriteLine(new string('-', 70));
        Console.WriteLine();

        await using var transport = new UdpMdnsTransport();
        await transport.StartAsync();

        var packetNumber = 0;
        var senders = new Dictionary<string, int>();

        transport.PacketReceived += packet =>
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

            if (!MentionsAirDrop(message))
            {
                return;
            }

            var source = packet.Source.Address;
            var isOwn = localAddresses.Any(a => a.Equals(source));

            var key = source.ToString();
            senders[key] = senders.GetValueOrDefault(key) + 1;

            packetNumber++;
            Console.ForegroundColor = isOwn ? ConsoleColor.DarkGray : ConsoleColor.Green;
            Console.WriteLine($"[{packetNumber:D3}] {source}{(isOwn ? "  (ESTE EQUIPO)" : string.Empty)}");
            Console.ResetColor();

            Console.WriteLine($"      tipo: {(message.Header.IsResponse ? "RESPUESTA" : "CONSULTA")}" +
                              $"   preguntas: {message.Questions.Count}" +
                              $"   respuestas: {message.Answers.Count}");

            foreach (var question in message.Questions)
            {
                var unicast = question.WantsUnicastResponse ? "  [pide unicast]" : string.Empty;
                Console.WriteLine($"      ? {question.Name}  ({question.Type}){unicast}");
            }

            foreach (var record in message.Answers.Concat(message.Additionals))
            {
                Console.WriteLine($"      > {DescribeRecord(record)}");
            }

            Console.WriteLine();
        };

        // Un responder real permite comprobar si contestar a esas consultas hace que el
        // dispositivo que pregunta nos muestre en su interfaz.
        MdnsResponder? responder = null;
        if (respond)
        {
            var registration = new AirDropServiceRegistration(
                AirDropServiceRegistration.GenerateInstanceId(),
                Environment.MachineName,
                localAddresses);

            responder = new MdnsResponder(new UdpMdnsTransport(), registration);
            await responder.StartAsync();

            Console.WriteLine($"Respondiendo como: {registration.InstanceName}");
            Console.WriteLine();
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));

        if (responder is not null)
        {
            await responder.DisposeAsync();
        }

        Console.WriteLine(new string('=', 70));
        Console.WriteLine("RESUMEN");
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"  Paquetes de AirDrop observados: {packetNumber}");
        Console.WriteLine();

        if (senders.Count == 0)
        {
            Console.WriteLine("  No se observó tráfico de AirDrop por la red de infraestructura.");
            return 0;
        }

        Console.WriteLine("  Por remitente:");
        foreach (var (address, count) in senders.OrderByDescending(p => p.Value))
        {
            var isOwn = localAddresses.Any(a => a.ToString() == address);
            Console.WriteLine($"    {address,-46} {count,4} paquetes{(isOwn ? "   (este equipo)" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine("  Interpretación:");
        Console.WriteLine("    - CONSULTA de _airdrop._tcp desde otra IP significa que ese");
        Console.WriteLine("      dispositivo busca receptores por la Wi-Fi de infraestructura.");
        Console.WriteLine("    - RESPUESTA desde otra IP significaría que se anuncia como");
        Console.WriteLine("      receptor por esa misma vía, lo que sería aún más relevante.");

        return 0;
    }

    private static bool MentionsAirDrop(DnsMessage message) =>
        message.Questions.Any(q => q.Name.Contains("airdrop", StringComparison.OrdinalIgnoreCase))
        || message.Answers.Concat(message.Additionals)
            .Any(r => r.Name.Contains("airdrop", StringComparison.OrdinalIgnoreCase)
                || (r is PtrRecord ptr
                    && ptr.Target.Contains("airdrop", StringComparison.OrdinalIgnoreCase)));

    private static string DescribeRecord(DnsResourceRecord record) => record switch
    {
        PtrRecord ptr => $"PTR  {ptr.Name} -> {ptr.Target}  (TTL {ptr.Ttl.TotalSeconds:0})",
        SrvRecord srv => $"SRV  {srv.Name} -> {srv.Target}:{srv.Port}  (TTL {srv.Ttl.TotalSeconds:0})",
        TxtRecord txt => $"TXT  {txt.Name}  [{string.Join(", ", txt.Strings)}]",
        AddressRecord address => $"{address.Type}  {address.Name} -> {address.Address}",
        _ => $"{record.Type}  {record.Name}",
    };

    private static List<IPAddress> GetLocalAddresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (!IPAddress.IsLoopback(unicast.Address))
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses;
    }
}
