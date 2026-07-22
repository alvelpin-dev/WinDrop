using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AirDrop.Client;
using AirDrop.Discovery.Mdns;
using AirDrop.Server;
using AirDrop.Server.Security;
using Microsoft.Extensions.Logging;

namespace WinDrop.Services;

/// <summary>
/// Orquesta el receptor, el descubrimiento y el emisor.
/// </summary>
/// <remarks>
/// Es la única clase que conoce a la vez todas las piezas del protocolo. La interfaz habla solo
/// con ella, y ninguna clase de protocolo sabe que existe una interfaz.
/// </remarks>
public sealed class AirDropService(
    Func<AppSettings> settingsProvider,
    FileReceiver receiver,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger<AirDropService> _logger = loggerFactory.CreateLogger<AirDropService>();

    private AirDropServer? _server;
    private MdnsResponder? _responder;
    private MdnsBrowser? _browser;
    private UdpMdnsTransport? _browserTransport;

    /// <summary>Si el receptor está activo y anunciándose.</summary>
    public bool IsReceiving => _server is not null;

    /// <summary>Puerto en el que escucha el receptor.</summary>
    public int Port => _server?.Port ?? 0;

    /// <summary>Se dispara cuando aparece o se actualiza un dispositivo en la red.</summary>
    public event Action<DiscoveredReceiver>? DeviceDiscovered;

    /// <summary>Se dispara cuando un dispositivo deja de estar disponible.</summary>
    public event Action<string>? DeviceLost;

    /// <summary>Arranca el receptor y empieza a anunciarse por mDNS.</summary>
    public async Task StartReceivingAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            return;
        }

        var settings = settingsProvider();
        Directory.CreateDirectory(settings.DownloadFolder);

        var certificate = AirDropCertificate.LoadOrCreate(
            Path.Combine(AppSettings.DataFolder, "identity.pfx"));

        _server = new AirDropServer(
            new AirDropServerOptions
            {
                Port = MdnsConstants.AirDropPort,
                Certificate = certificate,
            },
            receiver,
            loggerFactory);

        try
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // Causa habitual: otra instancia de la aplicación, o un servicio que ya ocupa el 8770.
            _logger.LogError(ex, "No se pudo abrir el puerto {Port}", MdnsConstants.AirDropPort);
            _server = null;
            throw new InvalidOperationException(
                $"El puerto {MdnsConstants.AirDropPort} está ocupado. " +
                "Comprueba que no haya otra copia de WinDrop en ejecución.", ex);
        }

        var registration = new AirDropServiceRegistration(
            AirDropServiceRegistration.GenerateInstanceId(),
            Environment.MachineName,
            GetLocalAddresses(),
            port: _server.Port);

        _responder = new MdnsResponder(
            new UdpMdnsTransport(loggerFactory.CreateLogger<UdpMdnsTransport>()),
            registration,
            loggerFactory.CreateLogger<MdnsResponder>());

        await _responder.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Recepción activa como '{Name}' en el puerto {Port}",
            settings.DeviceName,
            _server.Port);
    }

    /// <summary>Detiene el receptor y retira el anuncio.</summary>
    public async Task StopReceivingAsync()
    {
        if (_responder is not null)
        {
            await _responder.DisposeAsync().ConfigureAwait(false);
            _responder = null;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        _logger.LogInformation("Recepción detenida");
    }

    /// <summary>Empieza a buscar dispositivos a los que enviar.</summary>
    public async Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (_browser is not null)
        {
            await _browser.QueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _browserTransport = new UdpMdnsTransport(loggerFactory.CreateLogger<UdpMdnsTransport>());
        _browser = new MdnsBrowser(_browserTransport, loggerFactory.CreateLogger<MdnsBrowser>());

        _browser.ReceiverDiscovered += r => DeviceDiscovered?.Invoke(r);
        _browser.ReceiverLost += id => DeviceLost?.Invoke(id);

        await _browser.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vuelve a preguntar quién hay en la red.</summary>
    public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_browser is not null)
        {
            await _browser.QueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Resuelve el nombre legible de un dispositivo descubierto.</summary>
    public async Task<string?> ResolveDeviceNameAsync(
        DiscoveredReceiver device,
        CancellationToken cancellationToken = default)
    {
        // El nombre de instancia mDNS es opaco por diseño: el nombre real solo se obtiene
        // hablando por HTTPS con /Discover.
        using var client = new AirDropClient(
            new SenderIdentity(settingsProvider().DeviceName),
            loggerFactory.CreateLogger<AirDropClient>());

        foreach (var endpoint in EndpointsFor(device))
        {
            var identity = await client.DiscoverAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                return identity.ReceiverComputerName;
            }
        }

        return null;
    }

    /// <summary>Envía ficheros a un dispositivo descubierto.</summary>
    public async Task<TransferResult> SendAsync(
        DiscoveredReceiver device,
        IReadOnlyList<FileToSend> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new AirDropClient(
            new SenderIdentity(settingsProvider().DeviceName),
            loggerFactory.CreateLogger<AirDropClient>());

        // Un host puede anunciar varias direcciones (IPv4 e IPv6, o varias interfaces) y no todas
        // tienen por qué ser alcanzables desde aquí: se prueban en orden hasta que una funcione.
        TransferResult? lastResult = null;

        foreach (var endpoint in EndpointsFor(device))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await client.SendAsync(endpoint, files, progress, cancellationToken)
                .ConfigureAwait(false);

            // Un rechazo o una cancelación son respuestas definitivas: probar otra dirección
            // volvería a molestar al usuario del otro lado.
            if (result.Phase is TransferPhase.Completed
                or TransferPhase.Rejected
                or TransferPhase.Cancelled)
            {
                return result;
            }

            lastResult = result;
            _logger.LogDebug("Envío a {Endpoint} falló, probando la siguiente dirección", endpoint);
        }

        return lastResult ?? new TransferResult(
            TransferPhase.Failed,
            Error: new InvalidOperationException("El dispositivo no tiene direcciones alcanzables."));
    }

    /// <summary>Direcciones a probar, con IPv4 primero por ser la ruta más fiable en redes domésticas.</summary>
    private static IEnumerable<IPEndPoint> EndpointsFor(DiscoveredReceiver device) =>
        device.Addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => new IPEndPoint(a, device.Port));

    /// <summary>Direcciones locales anunciables: unicast, no loopback, de interfaces activas.</summary>
    private static List<IPAddress> GetLocalAddresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;

                if (IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                // Las link-local de IPv6 requieren índice de zona para ser útiles fuera del
                // equipo, así que se anuncian solo las globales y las IPv4.
                if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
                {
                    continue;
                }

                addresses.Add(ip);
            }
        }

        return addresses;
    }

    public async ValueTask DisposeAsync()
    {
        await StopReceivingAsync().ConfigureAwait(false);

        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
            _browser = null;
        }

        _browserTransport = null;
    }
}
