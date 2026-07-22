using System.IO;
using AirDrop.Core.Storage;
using AirDrop.Server;
using Microsoft.Extensions.Logging;

namespace WinDrop.Services;

/// <summary>Petición de confirmación que se eleva a la interfaz.</summary>
/// <param name="SenderName">Nombre del emisor.</param>
/// <param name="FileNames">Ficheros que quiere enviar.</param>
public sealed record AcceptancePrompt(string SenderName, IReadOnlyList<string> FileNames);

/// <summary>
/// Recibe las transferencias entrantes y las escribe en la carpeta configurada.
/// </summary>
/// <remarks>
/// Es la implementación de <see cref="IIncomingTransferHandler"/> que conecta el protocolo con la
/// interfaz y el disco. El servidor no sabe nada de esto.
/// </remarks>
public sealed class FileReceiver(
    Func<AppSettings> settingsProvider,
    Func<AcceptancePrompt, CancellationToken, Task<bool>> askUser,
    ILogger<FileReceiver> logger) : IIncomingTransferHandler
{
    /// <summary>Ficheros escritos en la transferencia en curso, para poder limpiarlos si falla.</summary>
    private readonly List<string> _writtenPaths = [];

    public ReceiverIdentity Identity => new(settingsProvider().DeviceName);

    /// <summary>Se dispara al completarse una transferencia.</summary>
    public event Action<IReadOnlyList<ReceivedFile>, string>? TransferCompleted;

    /// <summary>Se dispara si la transferencia falla.</summary>
    public event Action<Exception>? TransferFailed;

    /// <summary>Nombre del emisor de la transferencia en curso.</summary>
    private string _currentSender = "Desconocido";

    public async Task<bool> ShouldAcceptAsync(
        IncomingTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsProvider();

        // Con la visibilidad desactivada no se acepta nada, aunque alguien llegue a conectarse.
        if (settings.Visibility == DeviceVisibility.Off)
        {
            logger.LogInformation(
                "Transferencia rechazada automáticamente: la visibilidad está desactivada");
            return false;
        }

        _currentSender = request.Request.SenderComputerName;
        _writtenPaths.Clear();

        var prompt = new AcceptancePrompt(
            request.Request.SenderComputerName,
            [.. request.Request.Files.Select(f => f.FileName)]);

        return await askUser(prompt, cancellationToken).ConfigureAwait(false);
    }

    public Task<Stream> OpenFileForWritingAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var folder = settingsProvider().DownloadFolder;

        // Doble frontera: el servidor ya saneó el nombre, y aquí se vuelve a verificar contra la
        // carpeta real de destino. Es barato y cubre lo que el análisis textual no puede prever.
        var destination = SafeRelativePath.CombineWithin(folder, fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        destination = MakeUniquePath(destination);

        logger.LogInformation("Recibiendo '{File}' en '{Path}'", fileName, destination);

        _writtenPaths.Add(destination);
        return Task.FromResult<Stream>(File.Create(destination));
    }

    public Task OnTransferCompletedAsync(
        IReadOnlyList<ReceivedFile> files,
        CancellationToken cancellationToken = default)
    {
        // Se informan las rutas reales en disco, que pueden diferir de las anunciadas si hubo
        // que desambiguar nombres repetidos.
        var actual = files
            .Zip(_writtenPaths, (file, path) => file with { Path = path })
            .ToList();

        TransferCompleted?.Invoke(actual, _currentSender);
        return Task.CompletedTask;
    }

    public Task OnTransferFailedAsync(Exception error, CancellationToken cancellationToken = default)
    {
        logger.LogError(error, "Transferencia fallida de '{Sender}'", _currentSender);

        // Los ficheros a medio escribir se borran: dejarlos confundiría al usuario, que los vería
        // en su carpeta de descargas aparentemente completos.
        foreach (var path in _writtenPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "No se pudo borrar el fichero parcial '{Path}'", path);
            }
        }

        _writtenPaths.Clear();
        TransferFailed?.Invoke(error);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Añade un sufijo numérico si el fichero ya existe, en lugar de sobrescribir.
    /// </summary>
    /// <remarks>
    /// Sobrescribir sería destructivo y silencioso: alguien podría reemplazar un fichero del
    /// usuario simplemente enviando otro con el mismo nombre.
    /// </remarks>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fallback improbable: sufijo único por marca de tiempo.
        return Path.Combine(directory, $"{name} ({DateTime.Now:yyyyMMddHHmmss}){extension}");
    }
}
