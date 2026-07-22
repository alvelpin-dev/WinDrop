using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using AirDrop.Core.Archives;
using AirDrop.Core.Protocol.Messages;
using AirDrop.Core.Protocol.Plist;
using AirDrop.Core.Storage;
using AirDrop.Server.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AirDrop.Server;

/// <summary>Opciones del servidor AirDrop.</summary>
public sealed class AirDropServerOptions
{
    /// <summary>Puerto TCP. AirDrop usa el 8770.</summary>
    public int Port { get; init; } = 8770;

    /// <summary>Direcciones en las que escuchar. Vacío significa todas.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>Certificado TLS. Autofirmado es correcto aquí (ver <see cref="Security.AirDropCertificate"/>).</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    /// Tamaño máximo aceptado en un <c>/Upload</c>. Cero significa sin límite.
    /// </summary>
    /// <remarks>
    /// Por defecto sin límite: AirDrop se usa para vídeos de varios gigabytes y un tope arbitrario
    /// produciría fallos incomprensibles a mitad de transferencia.
    /// </remarks>
    public long MaxUploadBytes { get; init; }
}

/// <summary>
/// Receptor AirDrop: servidor HTTPS que atiende <c>/Discover</c>, <c>/Ask</c> y <c>/Upload</c>.
/// </summary>
/// <remarks>
/// <para>
/// El servidor no sabe nada de interfaz, carpetas ni notificaciones: habla el protocolo y delega
/// las decisiones en <see cref="IIncomingTransferHandler"/>.
/// </para>
/// <para>
/// Los tres endpoints comparten estado <b>por conexión TLS</b>, como hace el receptor de Apple.
/// Esto no es un detalle de implementación sino una medida de seguridad: sin él, cualquiera podría
/// hacer un <c>POST /Upload</c> directo y escribir ficheros sin que el usuario hubiera aceptado nada.
/// </para>
/// </remarks>
public sealed class AirDropServer : IAsyncDisposable
{
    private readonly AirDropServerOptions _options;
    private readonly IIncomingTransferHandler _handler;
    private readonly ILogger<AirDropServer> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Sesiones activas, indexadas por identificador de conexión de Kestrel.</summary>
    private readonly ConcurrentDictionary<string, TransferSession> _sessions = new();

    private WebApplication? _application;

    public AirDropServer(
        AirDropServerOptions options,
        IIncomingTransferHandler handler,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<AirDropServer>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AirDropServer>.Instance;
    }

    /// <summary>Puerto en el que está escuchando. Útil cuando se pide el puerto 0 en los tests.</summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        if (_loggerFactory is not null)
        {
            builder.Services.AddSingleton(_loggerFactory);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Sin límite de tamaño de cuerpo: AirDrop transfiere vídeos de varios gigabytes.
            kestrel.Limits.MaxRequestBodySize =
                _options.MaxUploadBytes > 0 ? _options.MaxUploadBytes : null;

            // /Ask permanece abierta mientras el usuario decide, que pueden ser minutos. Los
            // timeouts por defecto de Kestrel cortarían la petición antes de tiempo.
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);

            // Y una tasa mínima de datos no tiene sentido con un emisor que está esperando a que
            // una persona pulse un botón.
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.MinResponseDataRate = null;

            var addresses = _options.Addresses.Count > 0
                ? _options.Addresses
                : [IPAddress.IPv6Any];

            foreach (var address in addresses)
            {
                kestrel.Listen(address, _options.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.UseHttps(_options.Certificate);
                });
            }
        });

        _application = builder.Build();
        MapEndpoints(_application);

        await _application.StartAsync(cancellationToken).ConfigureAwait(false);

        Port = ResolveBoundPort() ?? _options.Port;

        _logger.LogInformation(
            "Receptor AirDrop escuchando en el puerto {Port} como '{Name}'",
            Port,
            _handler.Identity.ComputerName);
    }

    private void MapEndpoints(WebApplication application)
    {
        // El cast a Delegate es necesario: un manejador cuyo único parámetro es HttpContext se
        // interpretaría como RequestDelegate, que descarta el valor devuelto en lugar de
        // escribirlo en la respuesta.
        application.MapPost("/Discover", (Delegate)HandleDiscoverAsync);
        application.MapPost("/Ask", (Delegate)HandleAskAsync);
        application.MapPost("/Upload", (Delegate)HandleUploadAsync);

        // Los endpoints que no implementamos se responden explícitamente, para que quede en el log
        // si algún emisor los intenta usar en vez de fallar como un 404 anónimo.
        application.MapPost("/{endpoint}", (HttpContext context, string endpoint) =>
        {
            _logger.LogWarning(
                "Endpoint no implementado '/{Endpoint}' solicitado desde {Remote}",
                endpoint,
                context.Connection.RemoteIpAddress);

            return Results.StatusCode(StatusCodes.Status404NotFound);
        });
    }

    /// <summary>
    /// <c>POST /Discover</c>: el emisor se presenta y nosotros devolvemos nuestra identidad.
    /// </summary>
    /// <remarks>
    /// Esta respuesta es lo que hace que aparezcamos con nombre en la hoja de AirDrop del emisor.
    /// </remarks>
    private async Task<IResult> HandleDiscoverAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;

        DiscoverRequest request;
        try
        {
            request = DiscoverRequest.FromPlist(await ReadPlistAsync(context).ConfigureAwait(false));
        }
        catch (PlistFormatException ex)
        {
            _logger.LogWarning(ex, "/Discover con un plist ilegible desde {Remote}", remote);
            return Results.BadRequest();
        }

        // El SenderRecordData va firmado por la CA de Apple. Validarlo permitiría marcar al emisor
        // como verificado en la interfaz, pero no lo usamos para decidir el acceso: en modo
        // "Todos" un emisor legítimo puede no enviarlo (docs/01 §6.5).
        _logger.LogInformation(
            "/Discover desde {Remote} (identidad firmada: {HasRecord})",
            remote,
            request.SenderRecordData is not null);

        var response = new DiscoverResponse(
            _handler.Identity.ComputerName,
            _handler.Identity.ModelName);

        return PlistResult(response.ToPlist());
    }

    /// <summary>
    /// <c>POST /Ask</c>: el emisor pide permiso. La petición se mantiene abierta mientras el
    /// usuario decide.
    /// </summary>
    private async Task<IResult> HandleAskAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;

        AskRequest request;
        try
        {
            request = AskRequest.FromPlist(await ReadPlistAsync(context).ConfigureAwait(false));
        }
        catch (PlistFormatException ex)
        {
            _logger.LogWarning(ex, "/Ask con un plist ilegible desde {Remote}", remote);
            return Results.BadRequest();
        }

        _logger.LogInformation(
            "/Ask de '{Sender}' ({Model}) desde {Remote} con {FileCount} ficheros: {Files}",
            request.SenderComputerName,
            request.SenderModelName,
            remote,
            request.Files.Count,
            string.Join(", ", request.Files.Select(f => f.FileName)));

        bool accepted;
        try
        {
            accepted = await _handler.ShouldAcceptAsync(
                new IncomingTransferRequest(request, remote),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // El emisor canceló mientras el usuario decidía.
            _logger.LogInformation("/Ask cancelado por el emisor {Remote}", remote);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        if (!accepted)
        {
            _logger.LogInformation("Transferencia rechazada de '{Sender}'", request.SenderComputerName);
            _sessions.TryRemove(context.Connection.Id, out _);
            return Results.Unauthorized();
        }

        // Se marca la conexión como autorizada: sin esto, un /Upload directo escribiría ficheros
        // sin que el usuario hubiera aceptado nada.
        _sessions[context.Connection.Id] = new TransferSession(request, remote);

        _logger.LogInformation("Transferencia aceptada de '{Sender}'", request.SenderComputerName);

        var response = new AskResponse(
            _handler.Identity.ComputerName,
            _handler.Identity.ModelName);

        return PlistResult(response.ToPlist());
    }

    /// <summary>
    /// <c>POST /Upload</c>: llega el archivo CPIO con los ficheros.
    /// </summary>
    private async Task<IResult> HandleUploadAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;

        if (!_sessions.TryGetValue(context.Connection.Id, out var session))
        {
            // Nadie aceptó nada en esta conexión.
            _logger.LogWarning(
                "/Upload sin un /Ask aceptado previamente desde {Remote}: rechazado", remote);
            return Results.Unauthorized();
        }

        var received = new List<ReceivedFile>();

        try
        {
            await ExtractArchiveAsync(context, received).ConfigureAwait(false);

            _logger.LogInformation(
                "Transferencia completada de '{Sender}': {Count} ficheros, {Bytes} bytes",
                session.Request.SenderComputerName,
                received.Count,
                received.Sum(f => f.Length));

            await _handler.OnTransferCompletedAsync(received, context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Ok();
        }
        catch (Exception ex) when (ex is CpioFormatException or UnsafePathException or InvalidDataException)
        {
            _logger.LogError(ex, "Archivo inválido en /Upload desde {Remote}", remote);
            await NotifyFailureAsync(ex).ConfigureAwait(false);
            return Results.BadRequest();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo procesando /Upload desde {Remote}", remote);
            await NotifyFailureAsync(ex).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            _sessions.TryRemove(context.Connection.Id, out _);
        }
    }

    private async Task ExtractArchiveAsync(HttpContext context, List<ReceivedFile> received)
    {
        await using var body = await OpenDecompressedBodyAsync(context).ConfigureAwait(false);
        await using var reader = new CpioReader(body, leaveOpen: true);

        while (await reader.MoveNextAsync(context.RequestAborted).ConfigureAwait(false) is { } entry)
        {
            // Los directorios se materializan al crear los ficheros que contienen; los enlaces
            // simbólicos se descartan siempre, porque su destino podría apuntar fuera del
            // directorio de descargas.
            if (entry.IsDirectory)
            {
                continue;
            }

            if (entry.IsSymbolicLink)
            {
                _logger.LogWarning("Enlace simbólico descartado: '{Name}'", entry.Name);
                continue;
            }

            // Frontera de seguridad: el nombre lo elige el emisor.
            var safeName = SafeRelativePath.Normalize(entry.Name);

            await using var destination = await _handler
                .OpenFileForWritingAsync(safeName, context.RequestAborted)
                .ConfigureAwait(false);

            await reader.CopyEntryDataToAsync(
                destination, onProgress: null, context.RequestAborted).ConfigureAwait(false);

            received.Add(new ReceivedFile(
                Path.GetFileName(safeName),
                safeName,
                entry.DataLength));

            _logger.LogDebug("Recibido '{Name}' ({Bytes} bytes)", safeName, entry.DataLength);
        }
    }

    /// <summary>
    /// Abre el cuerpo de la petición, descomprimiéndolo si viene en gzip.
    /// </summary>
    /// <remarks>
    /// El cuerpo de <c>/Upload</c> llega comprimido <b>sin cabecera <c>Content-Encoding</c></b>
    /// (docs/01 §6.2), así que hay que detectarlo por el magic en vez de fiarse de las cabeceras.
    /// Se acepta también sin comprimir, porque no todos los emisores tienen por qué comprimir.
    /// </remarks>
    private async Task<Stream> OpenDecompressedBodyAsync(HttpContext context)
    {
        var magic = new byte[2];
        var read = await context.Request.Body
            .ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, context.RequestAborted)
            .ConfigureAwait(false);

        var body = new PrefixedStream(magic[..read], context.Request.Body);

        // Magic de gzip: 0x1F 0x8B.
        if (read == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            _logger.LogDebug("Cuerpo de /Upload comprimido con gzip");
            return new GZipStream(body, CompressionMode.Decompress);
        }

        _logger.LogDebug("Cuerpo de /Upload sin comprimir");
        return body;
    }

    private async Task NotifyFailureAsync(Exception error)
    {
        try
        {
            await _handler.OnTransferFailedAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Notificar un fallo no debe provocar otro.
            _logger.LogDebug(ex, "Fallo notificando el error de la transferencia");
        }
    }

    private static async Task<PlistDictionary> ReadPlistAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);

        // Un cuerpo vacío es un plist vacío: hay emisores que hacen /Discover sin contenido.
        return buffer.Length == 0
            ? new PlistDictionary()
            : BinaryPlistReader.ReadDictionary(buffer.ToArray());
    }

    private static IResult PlistResult(PlistDictionary plist) =>
        Results.Bytes(BinaryPlistWriter.Write(plist), "application/octet-stream");

    /// <summary>
    /// Averigua el puerto en el que Kestrel quedó escuchando.
    /// </summary>
    /// <remarks>
    /// Importa cuando se configura el puerto 0 y es el sistema quien asigna uno libre, que es lo
    /// que hacen los tests para no chocar entre sí ni con un AirDrop real en el 8770. La lista de
    /// direcciones la publica el propio servidor tras arrancar, no el contenedor de servicios.
    /// </remarks>
    private int? ResolveBoundPort()
    {
        var addresses = _application?.Services
            .GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;

        var first = addresses?.FirstOrDefault();
        return first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.StopAsync().ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
        }

        _sessions.Clear();
    }

    /// <summary>Estado de una transferencia autorizada, ligado a una conexión TLS.</summary>
    private sealed record TransferSession(AskRequest Request, IPAddress Sender);
}
