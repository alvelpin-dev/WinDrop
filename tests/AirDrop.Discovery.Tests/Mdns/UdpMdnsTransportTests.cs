using System.Net;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;
using Xunit;

namespace AirDrop.Discovery.Tests.Mdns;

/// <summary>
/// Tests de integración del transporte mDNS sobre sockets reales.
/// </summary>
/// <remarks>
/// A diferencia del resto de la suite, estos sí tocan la red de la máquina. Son el único sitio
/// donde se comprueba que el descubrimiento funciona de verdad y no solo en memoria, así que
/// merecen existir pese a depender del entorno. Si no hay red utilizable, se omiten en vez de
/// fallar: un fallo aquí debe significar "el código está mal", no "este equipo no tiene Wi-Fi".
/// </remarks>
[Trait("Category", "Integration")]
public class UdpMdnsTransportTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Transport_SendsAndReceivesOverRealSockets()
    {
        await using var sender = new UdpMdnsTransport();
        await using var receiver = new UdpMdnsTransport();

        try
        {
            await sender.StartAsync();
            await receiver.StartAsync();
        }
        catch (InvalidOperationException)
        {
            return;   // sin interfaces utilizables en este equipo
        }

        var received = new TaskCompletionSource<MdnsPacket>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Se filtra por nuestro tipo de servicio: la red local está llena de tráfico mDNS ajeno
        // y sin filtrar el test pasaría por casualidad con el primer paquete que llegue.
        receiver.PacketReceived += packet =>
        {
            try
            {
                var message = DnsMessageReader.Read(packet.Payload);
                if (message.Questions.Any(q => q.Name == MdnsConstants.AirDropServiceType))
                {
                    received.TrySetResult(packet);
                }
            }
            catch (DnsFormatException)
            {
                // Ruido de la red: se ignora.
            }
        };

        var query = DnsMessageWriter.Write(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        // Se repite: el multicast puede perder paquetes, y el objetivo es comprobar que el
        // camino funciona, no medir fiabilidad.
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        while (!timeout.IsCancellationRequested && !received.Task.IsCompleted)
        {
            await sender.SendAsync(query);
            await Task.WhenAny(received.Task, Task.Delay(250, CancellationToken.None));
        }

        Assert.True(
            received.Task.IsCompleted,
            "No se recibió el paquete multicast. Suele deberse al Firewall de Windows " +
            "bloqueando el UDP 5353 entrante.");

        var result = await received.Task;
        var decoded = DnsMessageReader.Read(result.Payload);
        Assert.Equal(MdnsConstants.AirDropServiceType, decoded.Questions[0].Name);
        Assert.Equal(DnsRecordType.Ptr, decoded.Questions[0].Type);
    }

    [Fact]
    public async Task ResponderAndTransport_AnnounceOverTheRealNetwork()
    {
        // Prueba de humo del camino completo: registro -> responder -> socket -> red.
        await using var listener = new UdpMdnsTransport();

        try
        {
            await listener.StartAsync();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var instanceId = AirDropServiceRegistration.GenerateInstanceId();
        var announced = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        listener.PacketReceived += packet =>
        {
            try
            {
                var message = DnsMessageReader.Read(packet.Payload);

                // Se busca nuestro identificador concreto, que es aleatorio y único de este test.
                if (message.Answers.Any(r => r.Name.Contains(instanceId, StringComparison.Ordinal)
                    || (r is PtrRecord ptr && ptr.Target.Contains(instanceId, StringComparison.Ordinal))))
                {
                    announced.TrySetResult(true);
                }
            }
            catch (DnsFormatException)
            {
                // Ruido de la red.
            }
        };

        var registration = new AirDropServiceRegistration(
            instanceId,
            "TestPC",
            [IPAddress.Loopback]);

        await using (var responder = new MdnsResponder(
            new UdpMdnsTransport(), registration, announcementInterval: TimeSpan.FromMilliseconds(200)))
        {
            await responder.StartAsync();
            await Task.WhenAny(announced.Task, Task.Delay(ReceiveTimeout));
        }

        Assert.True(
            announced.Task.IsCompleted,
            "No se recibió el anuncio. Suele deberse al Firewall de Windows bloqueando " +
            "el UDP 5353 entrante.");
    }

    [Fact]
    public async Task Dispose_IsSafeWithoutStarting()
    {
        var transport = new UdpMdnsTransport();

        await transport.DisposeAsync();
        await transport.DisposeAsync();   // idempotente
    }
}
