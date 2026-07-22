using System.Net;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;

namespace AirDrop.Discovery.Tests.Mdns;

/// <summary>
/// Transporte mDNS en memoria, para probar el responder y el browser sin tocar la red.
/// </summary>
/// <remarks>
/// Sin esto, los tests del descubrimiento dependerían de la configuración de red del equipo que
/// los ejecuta y darían fallos intermitentes imposibles de reproducir.
/// </remarks>
public sealed class FakeMdnsTransport : IMdnsTransport
{
    private readonly List<SentPacket> _sent = [];

    public event Action<MdnsPacket>? PacketReceived;

    /// <summary>Todo lo que se ha enviado, en orden.</summary>
    public IReadOnlyList<SentPacket> Sent => _sent;

    public bool IsStarted { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Si se establece, <see cref="SendAsync"/> lanza esta excepción.</summary>
    public Exception? SendFailure { get; set; }

    /// <summary>Los mensajes DNS enviados, ya decodificados.</summary>
    public IEnumerable<DnsMessage> SentMessages => _sent.Select(p => DnsMessageReader.Read(p.Payload));

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(
        byte[] payload,
        IPEndPoint? destination = null,
        CancellationToken cancellationToken = default)
    {
        if (SendFailure is not null)
        {
            return Task.FromException(SendFailure);
        }

        _sent.Add(new SentPacket(payload, destination));
        return Task.CompletedTask;
    }

    /// <summary>Simula la llegada de un mensaje desde la red.</summary>
    public void Receive(DnsMessage message, IPEndPoint? source = null) =>
        PacketReceived?.Invoke(new MdnsPacket(
            DnsMessageWriter.Write(message),
            source ?? new IPEndPoint(IPAddress.Parse("192.168.1.99"), MdnsConstants.Port)));

    /// <summary>Simula la llegada de bytes crudos, para probar el manejo de basura.</summary>
    public void ReceiveRaw(byte[] payload, IPEndPoint? source = null) =>
        PacketReceived?.Invoke(new MdnsPacket(
            payload,
            source ?? new IPEndPoint(IPAddress.Parse("192.168.1.99"), MdnsConstants.Port)));

    public void Clear() => _sent.Clear();

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public sealed record SentPacket(byte[] Payload, IPEndPoint? Destination)
    {
        public bool WasMulticast => Destination is null;

        public DnsMessage Decode() => DnsMessageReader.Read(Payload);
    }
}
