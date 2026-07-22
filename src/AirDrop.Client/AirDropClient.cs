using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using AirDrop.Core.Archives;
using AirDrop.Core.Protocol;
using AirDrop.Core.Protocol.Messages;
using AirDrop.Core.Protocol.Plist;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirDrop.Client;

/// <summary>Identidad con la que nos presentamos al enviar.</summary>
public sealed record SenderIdentity(string ComputerName, string ModelName = "Windows11,1");

/// <summary>Un fichero local seleccionado para enviar.</summary>
/// <param name="Path">Ruta en disco.</param>
/// <param name="Name">Nombre con el que llegará al receptor.</param>
public sealed record FileToSend(string Path, string Name)
{
    public static FileToSend FromPath(string path) =>
        new(path, System.IO.Path.GetFileName(path));

    public long Length => new FileInfo(Path).Length;
}

/// <summary>
/// Emisor AirDrop: ejecuta <c>/Discover</c> → <c>/Ask</c> → <c>/Upload</c> contra un receptor.
/// </summary>
/// <remarks>
/// <para>
/// Las tres peticiones viajan por la <b>misma conexión TLS</b>, como hace el emisor de Apple. No
/// es un detalle de eficiencia: el receptor asocia la autorización concedida en <c>/Ask</c> a la
/// conexión, así que abrir una conexión nueva para <c>/Upload</c> haría que la rechazara.
/// </para>
/// <para>
/// El certificado del receptor no se valida, igual que hace AirDrop: el TLS aporta cifrado, no
/// autenticación de peer (docs/01 §6.1). Los receptores usan certificados autofirmados y exigir
/// una cadena válida impediría cualquier transferencia.
/// </para>
/// </remarks>
public sealed class AirDropClient(
    SenderIdentity identity,
    ILogger<AirDropClient>? logger = null) : IDisposable
{
    private readonly ILogger<AirDropClient> _logger = logger ?? NullLogger<AirDropClient>.Instance;

    /// <summary>Cada envío usa su propio handler para garantizar una conexión TLS dedicada.</summary>
    private readonly List<HttpClient> _clients = [];

    /// <summary>
    /// Consulta la identidad de un receptor sin iniciar una transferencia.
    /// </summary>
    /// <remarks>
    /// Es lo que convierte un identificador mDNS opaco en un nombre legible para la interfaz.
    /// </remarks>
    public async Task<DiscoverResponse?> DiscoverAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(endpoint);

        try
        {
            var response = await PostPlistAsync(
                client, "/Discover", new DiscoverRequest(null).ToPlist(), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "/Discover a {Endpoint} respondió {Status}", endpoint, response.StatusCode);
                return null;
            }

            var plist = BinaryPlistReader.ReadDictionary(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

            return DiscoverResponse.FromPlist(plist);
        }
        catch (Exception ex) when (ex is HttpRequestException or PlistFormatException or IOException)
        {
            _logger.LogDebug(ex, "No se pudo consultar la identidad de {Endpoint}", endpoint);
            return null;
        }
    }

    /// <summary>
    /// Envía ficheros a un receptor.
    /// </summary>
    /// <param name="endpoint">Dirección y puerto del receptor.</param>
    /// <param name="files">Ficheros a enviar.</param>
    /// <param name="progress">Recibe las actualizaciones de estado.</param>
    public async Task<TransferResult> SendAsync(
        IPEndPoint endpoint,
        IReadOnlyList<FileToSend> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            throw new ArgumentException("No hay ficheros que enviar.", nameof(files));
        }

        // Una única instancia para todo el envío: las tres peticiones deben compartir conexión TLS.
        var client = CreateClient(endpoint);
        _clients.Add(client);

        try
        {
            return await SendCoreAsync(client, endpoint, files, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Envío a {Endpoint} cancelado", endpoint);
            progress?.Report(new TransferProgress(TransferPhase.Cancelled));
            return new TransferResult(TransferPhase.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo enviando a {Endpoint}", endpoint);
            progress?.Report(new TransferProgress(TransferPhase.Failed));
            return new TransferResult(TransferPhase.Failed, Error: ex);
        }
        finally
        {
            _clients.Remove(client);
            client.Dispose();
        }
    }

    private async Task<TransferResult> SendCoreAsync(
        HttpClient client,
        IPEndPoint endpoint,
        IReadOnlyList<FileToSend> files,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new TransferProgress(TransferPhase.Discovering));

        // /Discover es opcional en el protocolo. Se hace igualmente porque su respuesta da el
        // nombre legible del receptor, que es lo que se muestra en la interfaz.
        string? receiverName = null;
        var discoverResponse = await PostPlistAsync(
            client, "/Discover", new DiscoverRequest(null).ToPlist(), cancellationToken)
            .ConfigureAwait(false);

        if (discoverResponse.IsSuccessStatusCode)
        {
            try
            {
                receiverName = DiscoverResponse.FromPlist(BinaryPlistReader.ReadDictionary(
                    await discoverResponse.Content.ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false))).ReceiverComputerName;
            }
            catch (PlistFormatException ex)
            {
                // Un /Discover ilegible no impide transferir: solo perdemos el nombre bonito.
                _logger.LogDebug(ex, "Respuesta de /Discover ilegible desde {Endpoint}", endpoint);
            }
        }

        _logger.LogInformation(
            "Enviando {Count} ficheros a '{Receiver}' ({Endpoint})",
            files.Count,
            receiverName ?? "desconocido",
            endpoint);

        // El receptor mantiene esta petición abierta mientras su usuario decide.
        progress?.Report(new TransferProgress(TransferPhase.WaitingForAcceptance, CurrentFileName: receiverName));

        var ask = new AskRequest(
            identity.ComputerName,
            identity.ModelName,
            [.. files.Select(f => AirDropFileMetadata.ForFile(
                f.Name, UniformTypeIdentifiers.ForFileName(f.Name)))],
            BundleId: "com.windrop.app");

        var askResponse = await PostPlistAsync(client, "/Ask", ask.ToPlist(), cancellationToken)
            .ConfigureAwait(false);

        if (askResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("Transferencia rechazada por '{Receiver}'", receiverName);
            progress?.Report(new TransferProgress(TransferPhase.Rejected));
            return new TransferResult(TransferPhase.Rejected, receiverName);
        }

        if (!askResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"/Ask respondió {(int)askResponse.StatusCode} {askResponse.StatusCode}.");
        }

        await UploadAsync(client, files, progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Transferencia completada a '{Receiver}'", receiverName);
        progress?.Report(new TransferProgress(
            TransferPhase.Completed, TotalBytes(files), TotalBytes(files)));

        return new TransferResult(TransferPhase.Completed, receiverName);
    }

    private async Task UploadAsync(
        HttpClient client,
        IReadOnlyList<FileToSend> files,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = TotalBytes(files);
        progress?.Report(new TransferProgress(TransferPhase.Uploading, 0, total));

        // El cuerpo se genera al vuelo mientras se envía: materializar en memoria un archivo de
        // varios gigabytes no es viable, y AirDrop se usa precisamente para vídeos grandes.
        using var content = new PushStreamContent(
            stream => WriteArchiveAsync(stream, files, total, progress, cancellationToken));

        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await client.PostAsync("/Upload", content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"/Upload respondió {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    /// <summary>Escribe el archivo CPIO comprimido con gzip directamente en el cuerpo de la petición.</summary>
    private static async Task WriteArchiveAsync(
        Stream destination,
        IReadOnlyList<FileToSend> files,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // El receptor espera gzip aunque no se anuncie con Content-Encoding (docs/01 §6.2).
        await using var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true);
        await using var writer = new CpioWriter(gzip, CpioFormat.Odc, leaveOpen: true);

        const int regularFile644 = 0x81A4;
        long sentBefore = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file.Path);
            var entry = new CpioEntry(
                $"./{file.Name}",
                regularFile644,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

            await using var source = File.OpenRead(file.Path);

            var fileProgress = progress is null
                ? null
                : new Progress<long>(sent => progress.Report(new TransferProgress(
                    TransferPhase.Uploading, sentBefore + sent, total, file.Name)));

            await writer.WriteEntryAsync(entry, source, fileProgress, cancellationToken)
                .ConfigureAwait(false);

            sentBefore += info.Length;
        }

        await writer.WriteTrailerAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long TotalBytes(IReadOnlyList<FileToSend> files) =>
        files.Sum(f => new FileInfo(f.Path).Length);

    private static async Task<HttpResponseMessage> PostPlistAsync(
        HttpClient client,
        string path,
        PlistDictionary plist,
        CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(BinaryPlistWriter.Write(plist));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateClient(IPEndPoint endpoint)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Los receptores AirDrop usan certificados autofirmados: exigir una cadena válida
                // impediría toda transferencia. El TLS aquí cifra, no autentica.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },

            // Una sola conexión: la autorización de /Ask va ligada a ella en el receptor.
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        };

        var host = endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address}]"
            : endpoint.Address.ToString();

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{endpoint.Port}/"),
            // Sin timeout global: una transferencia grande, o un usuario que tarda en aceptar,
            // superan cualquier valor razonable. La cancelación se controla con el token.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Dispose()
    {
        foreach (var client in _clients.ToList())
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}

/// <summary>
/// Contenido HTTP que se genera al vuelo mientras se envía.
/// </summary>
/// <remarks>
/// Evita construir en memoria el archivo completo antes de mandarlo, que con vídeos de varios
/// gigabytes sencillamente no funcionaría.
/// </remarks>
internal sealed class PushStreamContent(Func<Stream, Task> writer) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        writer(stream);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        writer(stream);

    protected override bool TryComputeLength(out long length)
    {
        // El tamaño comprimido no se conoce por adelantado: se envía con codificación por bloques.
        length = 0;
        return false;
    }
}
