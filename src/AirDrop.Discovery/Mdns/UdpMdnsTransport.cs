using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirDrop.Discovery.Mdns;

/// <summary>
/// Transporte mDNS sobre sockets UDP multicast reales.
/// </summary>
/// <remarks>
/// <para>
/// Abre un socket por familia de direcciones y se une al grupo multicast en todas las interfaces
/// activas. Un solo socket no basta: en un equipo con Wi-Fi y Ethernet, unirse solo a la interfaz
/// por defecto haría que no viéramos a los dispositivos de la otra red.
/// </para>
/// <para>
/// El puerto se abre con <see cref="SocketOptionName.ReuseAddress"/> a propósito, para convivir
/// con otros responders mDNS del sistema —típicamente el Bonjour que instala iTunes— en lugar de
/// fallar al arrancar porque el 5353 ya está ocupado.
/// </para>
/// </remarks>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    /// <summary>Tamaño del buffer de recepción. Un datagrama mDNS no debería pasar de 9 KB.</summary>
    private const int ReceiveBufferSize = 9000;

    private readonly ILogger<UdpMdnsTransport> _logger;
    private readonly List<BoundSocket> _sockets = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _receiveLoops = [];

    private bool _started;

    public UdpMdnsTransport(ILogger<UdpMdnsTransport>? logger = null) =>
        _logger = logger ?? NullLogger<UdpMdnsTransport>.Instance;

    public event Action<MdnsPacket>? PacketReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;

        var interfaces = GetUsableInterfaces();
        if (interfaces.Count == 0)
        {
            _logger.LogWarning("No hay interfaces de red utilizables: el descubrimiento no funcionará");
        }

        TryBind(AddressFamily.InterNetwork, interfaces);
        TryBind(AddressFamily.InterNetworkV6, interfaces);

        if (_sockets.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo abrir ningún socket mDNS. Comprueba que el puerto 5353 esté accesible.");
        }

        foreach (var socket in _sockets)
        {
            _receiveLoops.Add(ReceiveLoopAsync(socket, _shutdown.Token));
        }

        _logger.LogInformation(
            "Transporte mDNS activo en {SocketCount} sockets sobre {InterfaceCount} interfaces",
            _sockets.Count,
            interfaces.Count);

        return Task.CompletedTask;
    }

    public async Task SendAsync(
        byte[] payload,
        IPEndPoint? destination = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (destination is not null)
        {
            await SendUnicastAsync(payload, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Multicast: se emite por todos los sockets. Si una interfaz falla, las demás deben
        // seguir funcionando, así que los fallos se registran pero no se propagan.
        foreach (var socket in _sockets)
        {
            var group = socket.Family == AddressFamily.InterNetwork
                ? MdnsConstants.MulticastEndPointV4
                : MdnsConstants.MulticastEndPointV6;

            try
            {
                await socket.Socket.SendToAsync(payload, SocketFlags.None, group, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(
                    ex, "No se pudo emitir por multicast en {Family}", socket.Family);
            }
        }
    }

    private async Task SendUnicastAsync(
        byte[] payload,
        IPEndPoint destination,
        CancellationToken cancellationToken)
    {
        var socket = _sockets.FirstOrDefault(s => s.Family == destination.AddressFamily);
        if (socket is null)
        {
            _logger.LogDebug(
                "Sin socket para responder por unicast a {Destination}", destination);
            return;
        }

        try
        {
            await socket.Socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "No se pudo responder por unicast a {Destination}", destination);
        }
    }

    private void TryBind(AddressFamily family, IReadOnlyList<NetworkInterface> interfaces)
    {
        Socket? socket = null;

        try
        {
            socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);

            // Convivir con otros responders del sistema en vez de pelearnos por el puerto.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // El loopback permite que dos instancias en el mismo equipo se vean, que es
            // justamente el escenario de prueba Windows <-> Windows.
            socket.SetSocketOption(
                family == AddressFamily.InterNetwork
                    ? SocketOptionLevel.IP
                    : SocketOptionLevel.IPv6,
                SocketOptionName.MulticastLoopback,
                true);

            var bindAddress = family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            socket.Bind(new IPEndPoint(bindAddress, MdnsConstants.Port));

            var joined = JoinMulticastGroups(socket, family, interfaces);
            if (joined == 0)
            {
                _logger.LogDebug(
                    "Ninguna interfaz aceptó unirse al grupo multicast en {Family}", family);
                socket.Dispose();
                return;
            }

            _sockets.Add(new BoundSocket(socket, family));
            _logger.LogDebug(
                "Socket mDNS {Family} unido al grupo en {Count} interfaces", family, joined);
        }
        catch (SocketException ex)
        {
            // Que falle IPv6 en un equipo sin IPv6 es normal y no debe impedir el arranque.
            _logger.LogDebug(ex, "No se pudo abrir el socket mDNS para {Family}", family);
            socket?.Dispose();
        }
    }

    private int JoinMulticastGroups(
        Socket socket,
        AddressFamily family,
        IReadOnlyList<NetworkInterface> interfaces)
    {
        var joined = 0;

        foreach (var networkInterface in interfaces)
        {
            try
            {
                var properties = networkInterface.GetIPProperties();

                if (family == AddressFamily.InterNetwork)
                {
                    var v4 = properties.GetIPv4Properties();
                    if (v4 is null)
                    {
                        continue;
                    }

                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MdnsConstants.MulticastAddressV4, v4.Index));
                }
                else
                {
                    var v6 = properties.GetIPv6Properties();
                    if (v6 is null)
                    {
                        continue;
                    }

                    socket.SetSocketOption(
                        SocketOptionLevel.IPv6,
                        SocketOptionName.AddMembership,
                        new IPv6MulticastOption(MdnsConstants.MulticastAddressV6, v6.Index));
                }

                joined++;
            }
            catch (Exception ex) when (ex is SocketException or NetworkInformationException)
            {
                // Una interfaz que no admite multicast (adaptadores virtuales, VPNs) se salta:
                // no es motivo para dejar sin descubrimiento a las que sí funcionan.
                _logger.LogTrace(
                    ex, "La interfaz {Interface} no aceptó el grupo multicast", networkInterface.Name);
            }
        }

        return joined;
    }

    private async Task ReceiveLoopAsync(BoundSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        var anyEndPoint = new IPEndPoint(
            socket.Family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.Socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, cancellationToken)
                    .ConfigureAwait(false);

                if (result.ReceivedBytes == 0)
                {
                    continue;
                }

                var payload = buffer[..result.ReceivedBytes];
                PacketReceived?.Invoke(new MdnsPacket(payload, (IPEndPoint)result.RemoteEndPoint));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Un error puntual del socket (interfaz que desaparece, ICMP de puerto
                // inalcanzable) no debe terminar el bucle: dejaría el descubrimiento muerto
                // en silencio hasta reiniciar la aplicación.
                _logger.LogDebug(ex, "Error recibiendo en el socket {Family}", socket.Family);
            }
        }
    }

    /// <summary>Interfaces activas capaces de multicast, excluyendo el loopback.</summary>
    private static List<NetworkInterface> GetUsableInterfaces() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(i =>
                i.OperationalStatus == OperationalStatus.Up
                && i.SupportsMulticast
                && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)];

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var socket in _sockets)
        {
            socket.Socket.Dispose();
        }

        // Se espera a los bucles para no dejar tareas tocando sockets ya liberados.
        try
        {
            await Task.WhenAll(_receiveLoops).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Los bucles de recepción terminaron con error");
        }

        _sockets.Clear();
        _receiveLoops.Clear();
        _shutdown.Dispose();
        _started = false;
    }

    private sealed record BoundSocket(Socket Socket, AddressFamily Family);
}
