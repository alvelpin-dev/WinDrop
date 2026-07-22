using System.Collections.Concurrent;
using System.Net;
using AirDrop.Core.Protocol;
using AirDrop.Discovery.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirDrop.Discovery.Mdns;

/// <summary>Un receptor AirDrop descubierto en la red.</summary>
/// <param name="InstanceName">Nombre de instancia mDNS, opaco.</param>
/// <param name="HostName">Nombre del host que resuelve a las direcciones.</param>
/// <param name="Port">Puerto del servidor HTTPS.</param>
/// <param name="Addresses">Direcciones conocidas del host.</param>
/// <param name="Flags">Capacidades anunciadas en el TXT.</param>
public sealed record DiscoveredReceiver(
    string InstanceName,
    string? HostName,
    ushort Port,
    IReadOnlyList<IPAddress> Addresses,
    AirDropFlags Flags)
{
    /// <summary>
    /// Indica si el registro tiene lo mínimo para intentar una conexión.
    /// </summary>
    /// <remarks>
    /// Los registros de un servicio llegan en paquetes distintos y en cualquier orden, así que un
    /// receptor pasa por un estado incompleto antes de ser utilizable.
    /// </remarks>
    public bool IsUsable => Port > 0 && Addresses.Count > 0;

    /// <summary>Identificador estable para la interfaz, extraído del nombre de instancia.</summary>
    public string Id => InstanceName;
}

/// <summary>
/// Descubre receptores AirDrop en la red mediante mDNS.
/// </summary>
/// <remarks>
/// <para>
/// Un servicio DNS-SD no llega en un único paquete: el PTR, el SRV, el TXT y las direcciones
/// pueden venir por separado y en cualquier orden. El browser va acumulando lo que sabe de cada
/// instancia y avisa cuando pasa a ser utilizable, en lugar de exigir que todo llegue junto.
/// </para>
/// <para>
/// Recordatorio del protocolo: el nombre de instancia es un identificador opaco, no el nombre del
/// dispositivo. El nombre legible se obtiene después, por HTTPS, con <c>/Discover</c>.
/// </para>
/// </remarks>
public sealed class MdnsBrowser : IAsyncDisposable
{
    private readonly IMdnsTransport _transport;
    private readonly ILogger<MdnsBrowser> _logger;
    private readonly ConcurrentDictionary<string, ReceiverBuilder> _receivers = new(
        StringComparer.OrdinalIgnoreCase);

    private bool _started;

    public MdnsBrowser(IMdnsTransport transport, ILogger<MdnsBrowser>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? NullLogger<MdnsBrowser>.Instance;
    }

    /// <summary>Se dispara cuando un receptor pasa a ser utilizable o cambian sus datos.</summary>
    public event Action<DiscoveredReceiver>? ReceiverDiscovered;

    /// <summary>Se dispara cuando un receptor se retira (despedida con TTL cero).</summary>
    public event Action<string>? ReceiverLost;

    /// <summary>Receptores utilizables conocidos ahora mismo.</summary>
    public IReadOnlyList<DiscoveredReceiver> Receivers =>
        [.. _receivers.Values.Select(b => b.Build()).Where(r => r.IsUsable)];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _transport.PacketReceived += OnPacketReceived;
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        await QueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Emite una consulta PTR pidiendo que se identifiquen los receptores AirDrop.</summary>
    public async Task QueryAsync(CancellationToken cancellationToken = default)
    {
        var query = DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr));

        await _transport.SendAsync(DnsMessageWriter.Write(query), null, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Consulta de descubrimiento AirDrop enviada");
    }

    private void OnPacketReceived(MdnsPacket packet)
    {
        DnsMessage message;
        try
        {
            message = DnsMessageReader.Read(packet.Payload);
        }
        catch (DnsFormatException ex)
        {
            _logger.LogTrace(ex, "Paquete mDNS ilegible de {Source}", packet.Source);
            return;
        }

        if (!message.Header.IsResponse)
        {
            return;
        }

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in message.Answers.Concat(message.Additionals))
        {
            switch (record)
            {
                case PtrRecord ptr when IsAirDropService(ptr.Name):
                    if (ptr.Ttl == TimeSpan.Zero)
                    {
                        Forget(ptr.Target);
                        continue;
                    }

                    GetBuilder(ptr.Target);
                    touched.Add(ptr.Target);
                    break;

                case SrvRecord srv when IsAirDropInstance(srv.Name):
                    if (srv.Ttl == TimeSpan.Zero)
                    {
                        Forget(srv.Name);
                        continue;
                    }

                    var builder = GetBuilder(srv.Name);
                    builder.HostName = srv.Target;
                    builder.Port = srv.Port;
                    touched.Add(srv.Name);
                    break;

                case TxtRecord txt when IsAirDropInstance(txt.Name):
                    GetBuilder(txt.Name).Flags = ParseFlags(txt);
                    touched.Add(txt.Name);
                    break;

                case AddressRecord address:
                    // Las direcciones vienen indexadas por nombre de host, que hay que casar
                    // después con el SRV correspondiente.
                    if (!addressesByHost.TryGetValue(address.Name, out var list))
                    {
                        list = [];
                        addressesByHost[address.Name] = list;
                    }

                    list.Add(address.Address);
                    break;
            }
        }

        ApplyAddresses(addressesByHost, touched);
        NotifyUsable(touched);
    }

    /// <summary>
    /// Asocia las direcciones recibidas a los receptores cuyo host coincide.
    /// </summary>
    /// <remarks>
    /// Se aplican a todos los receptores conocidos, no solo a los tocados en este paquete: un
    /// anuncio de direcciones puede llegar por separado, después del SRV que las necesitaba.
    /// </remarks>
    private void ApplyAddresses(
        Dictionary<string, List<IPAddress>> addressesByHost,
        HashSet<string> touched)
    {
        if (addressesByHost.Count == 0)
        {
            return;
        }

        foreach (var (instanceName, builder) in _receivers)
        {
            if (builder.HostName is null
                || !addressesByHost.TryGetValue(builder.HostName, out var addresses))
            {
                continue;
            }

            foreach (var address in addresses)
            {
                if (builder.Addresses.Add(address))
                {
                    touched.Add(instanceName);
                }
            }
        }
    }

    private void NotifyUsable(HashSet<string> touched)
    {
        foreach (var instanceName in touched)
        {
            if (!_receivers.TryGetValue(instanceName, out var builder))
            {
                continue;
            }

            var receiver = builder.Build();
            if (!receiver.IsUsable)
            {
                continue;
            }

            _logger.LogInformation(
                "Receptor AirDrop descubierto: {Instance} en {Host}:{Port}",
                receiver.InstanceName,
                receiver.HostName,
                receiver.Port);

            ReceiverDiscovered?.Invoke(receiver);
        }
    }

    private void Forget(string instanceName)
    {
        if (_receivers.TryRemove(instanceName, out _))
        {
            _logger.LogInformation("Receptor AirDrop retirado: {Instance}", instanceName);
            ReceiverLost?.Invoke(instanceName);
        }
    }

    private ReceiverBuilder GetBuilder(string instanceName) =>
        _receivers.GetOrAdd(instanceName, name => new ReceiverBuilder(name));

    private static AirDropFlags ParseFlags(TxtRecord txt)
    {
        var pairs = txt.ToPairs();
        return pairs.TryGetValue("flags", out var raw) && int.TryParse(raw, out var value)
            ? (AirDropFlags)value
            : AirDropFlags.None;
    }

    private static bool IsAirDropService(string name) =>
        name.TrimEnd('.').Equals(MdnsConstants.AirDropServiceType, StringComparison.OrdinalIgnoreCase);

    private static bool IsAirDropInstance(string name) =>
        name.EndsWith(MdnsConstants.AirDropServiceType, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            _transport.PacketReceived -= OnPacketReceived;
            _started = false;
        }

        _receivers.Clear();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Acumula los registros de una instancia hasta que forman un receptor utilizable.</summary>
    private sealed class ReceiverBuilder(string instanceName)
    {
        public string InstanceName { get; } = instanceName;

        public string? HostName { get; set; }

        public ushort Port { get; set; }

        public HashSet<IPAddress> Addresses { get; } = [];

        public AirDropFlags Flags { get; set; }

        public DiscoveredReceiver Build() =>
            new(InstanceName, HostName, Port, [.. Addresses], Flags);
    }
}
