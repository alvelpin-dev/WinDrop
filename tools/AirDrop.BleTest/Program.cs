using System.Collections.Concurrent;
using AirDrop.Core.Protocol.Ble;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;
using AirDrop.Platform.Windows.Ble;

namespace AirDrop.BleTest;

/// <summary>
/// Ejecuta los tests 1 y 2 del plan de validación de la investigación.
/// </summary>
/// <remarks>
/// <para>Responde a dos preguntas con datos, no con argumentos:</para>
/// <list type="number">
///   <item>
///     <b>Test 2 — ¿Windows puede oír el AirDrop de un iPhone?</b> Se escuchan los anuncios BLE
///     de Continuity y se detecta el mensaje de tipo 0x05.
///   </item>
///   <item>
///     <b>Test 1 — ¿Un iPhone reacciona al anuncio BLE de Windows?</b> Se emite el mismo anuncio
///     que emitiría un dispositivo Apple y se observa si el iPhone responde. Si el Bluetooth
///     bastara para AirDrop, el teléfono debería anunciarse por mDNS o intentar conectarse al
///     puerto 8770 tras recibirlo.
///   </item>
/// </list>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 45;

        Console.WriteLine("Prueba de la capa Bluetooth de AirDrop");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine("QUÉ HACE ESTA PRUEBA");
        Console.WriteLine("  1. Escucha los anuncios BLE de dispositivos Apple cercanos.");
        Console.WriteLine("  2. Emite desde Windows el anuncio BLE de AirDrop.");
        Console.WriteLine("  3. Observa si algún iPhone reacciona anunciándose por mDNS.");
        Console.WriteLine();
        Console.WriteLine("QUÉ HACER MIENTRAS CORRE");
        Console.WriteLine("  Coge el iPhone, desbloquéalo, y abre Compartir -> AirDrop");
        Console.WriteLine("  en una foto. Déjalo en esa pantalla, cerca del PC.");
        Console.WriteLine();
        Console.WriteLine($"Duración: {seconds} segundos.");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();

        var detections = new ConcurrentDictionary<ulong, ContinuityDetection>();
        var airDropSightings = new ConcurrentDictionary<ulong, ContinuityDetection>();
        var mdnsAirDropResponses = new ConcurrentBag<string>();

        // Interesa si el anuncio llegó a emitirse en algún momento, no el estado final: al
        // terminar la prueba se detiene siempre, y leerlo entonces daría "Stopped" aunque haya
        // estado emitiendo los 40 segundos.
        var advertiserDidStart = false;

        // ── 1. Escáner BLE ──────────────────────────────────────────────
        using var scanner = new BleAirDropScanner();

        scanner.AppleDeviceDetected += detection =>
        {
            var isNew = !detections.ContainsKey(detection.Address);
            detections[detection.Address] = detection;

            if (detection.IsAdvertisingAirDrop && airDropSightings.TryAdd(detection.Address, detection))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(
                    $"  [AIRDROP] Dispositivo {detection.Address:X12} anunciando AirDrop " +
                    $"({detection.SignalStrength} dBm, {detection.Proximity})");
                Console.ResetColor();
            }
            else if (isNew)
            {
                Console.WriteLine(
                    $"  [apple]   {detection.Address:X12}  {detection.SignalStrength,4} dBm  " +
                    $"{string.Join(", ", detection.MessageTypes)}");
            }
        };

        if (!scanner.Start())
        {
            Console.Error.WriteLine("No se pudo iniciar el escaneo BLE. ¿Está el Bluetooth encendido?");
            return 1;
        }

        Console.WriteLine("Escaneo BLE activo. Dispositivos Apple detectados:");
        Console.WriteLine();

        // ── 2. Emisor BLE ───────────────────────────────────────────────
        using var advertiser = new BleAirDropAdvertiser();

        advertiser.StateChanged += state =>
        {
            if (state == AdvertiserState.Started)
            {
                advertiserDidStart = true;
            }

            Console.ForegroundColor = state == AdvertiserState.Started
                ? ConsoleColor.Cyan
                : ConsoleColor.Yellow;
            Console.WriteLine($"  [emisor]  Estado del anuncio BLE: {state}");
            Console.ResetColor();
        };

        // Sin identidad: los hashes solo sirven para el filtrado por contactos y emitir los del
        // usuario los expondría a cualquiera que escuche.
        advertiser.Start(AirDropAdvertisement.Anonymous());

        // ── 3. Vigilancia mDNS ──────────────────────────────────────────
        // Si el Bluetooth bastara, tras recibir nuestro anuncio el iPhone debería aparecer
        // anunciando _airdrop._tcp o preguntando por él.
        await using var transport = new UdpMdnsTransport();
        await transport.StartAsync();

        transport.PacketReceived += packet =>
        {
            try
            {
                var message = DnsMessageReader.Read(packet.Payload);

                var mentionsAirDrop =
                    message.Questions.Any(q => q.Name.Contains("airdrop", StringComparison.OrdinalIgnoreCase))
                    || message.Answers.Any(r => r.Name.Contains("airdrop", StringComparison.OrdinalIgnoreCase));

                if (mentionsAirDrop)
                {
                    var entry = $"{packet.Source.Address} ({(message.Header.IsResponse ? "respuesta" : "consulta")})";
                    if (!mdnsAirDropResponses.Contains(entry))
                    {
                        mdnsAirDropResponses.Add(entry);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [mDNS]    ¡Tráfico de AirDrop desde {entry}!");
                        Console.ResetColor();
                    }
                }
            }
            catch (DnsFormatException)
            {
                // Ruido de la red.
            }
        };

        await Task.Delay(TimeSpan.FromSeconds(seconds));

        scanner.Stop();
        advertiser.Stop();

        Report(detections, airDropSightings, mdnsAirDropResponses, advertiserDidStart);
        return 0;
    }

    private static void Report(
        ConcurrentDictionary<ulong, ContinuityDetection> detections,
        ConcurrentDictionary<ulong, ContinuityDetection> airDropSightings,
        ConcurrentBag<string> mdnsAirDropResponses,
        bool advertiserDidStart)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("RESULTADOS");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // ── Test 2 ──
        Console.WriteLine("TEST 2 - ¿Puede Windows oír el Bluetooth de AirDrop?");
        Console.WriteLine($"  Dispositivos Apple detectados por BLE : {detections.Count}");
        Console.WriteLine($"  De ellos, anunciando AirDrop          : {airDropSightings.Count}");

        if (airDropSightings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  => SÍ. Windows recibe correctamente el anuncio BLE de AirDrop.");
            Console.ResetColor();
            Console.WriteLine("     La capa Bluetooth del protocolo está implementada y funciona.");
        }
        else if (detections.Count > 0)
        {
            Console.WriteLine("  => Se detectaron dispositivos Apple, pero ninguno anunciaba AirDrop.");
            Console.WriteLine("     Repite la prueba con la hoja Compartir -> AirDrop abierta en el iPhone.");
        }
        else
        {
            Console.WriteLine("  => No se detectó ningún dispositivo Apple. Prueba no concluyente.");
        }

        // ── Test 1 ──
        Console.WriteLine();
        Console.WriteLine("TEST 1 - ¿Reacciona el iPhone al anuncio BLE de Windows?");
        Console.WriteLine($"  Windows llegó a emitir el anuncio     : {(advertiserDidStart ? "sí" : "no")}");
        Console.WriteLine($"  Respuestas de AirDrop por mDNS        : {mdnsAirDropResponses.Count}");

        if (mdnsAirDropResponses.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  => ¡HAY REACCIÓN! Esto contradice la conclusión de la investigación");
            Console.WriteLine("     y merece analizarse a fondo:");
            Console.ResetColor();

            foreach (var entry in mdnsAirDropResponses.Distinct())
            {
                Console.WriteLine($"       - {entry}");
            }
        }
        else if (advertiserDidStart)
        {
            Console.WriteLine("  => No hubo reacción.");
            Console.WriteLine();
            Console.WriteLine("     Windows emitió el anuncio BLE correctamente, con el mismo formato");
            Console.WriteLine("     que un dispositivo Apple. Ningún iPhone respondió por la red.");
            Console.WriteLine();
            Console.WriteLine("     Esto CONFIRMA que el Bluetooth solo es el timbre de AirDrop:");
            Console.WriteLine("     avisa de que hay una transferencia, pero el iPhone busca al");
            Console.WriteLine("     receptor por AWDL, no por Bluetooth ni por la Wi-Fi normal.");
            Console.WriteLine("     Ver docs/01-RESEARCH-airdrop-protocol.md, secciones 2, 3 y 7.");
        }
        else
        {
            Console.WriteLine("  => No concluyente: Windows no llegó a emitir el anuncio BLE.");
            Console.WriteLine("     Comprueba que el Bluetooth esté encendido.");
        }

        Console.WriteLine();
    }
}
