using AirDrop.Server;

namespace AirDrop.Server.Tests;

/// <summary>
/// Handler de pruebas que registra lo ocurrido y permite controlar la decisión de aceptación.
/// </summary>
internal sealed class RecordingTransferHandler : IIncomingTransferHandler
{
    private readonly Dictionary<string, MemoryStream> _files = [];

    public ReceiverIdentity Identity { get; init; } = new("PC de pruebas", "Windows11,1");

    /// <summary>Qué responder en <c>/Ask</c>.</summary>
    public bool Accept { get; set; } = true;

    /// <summary>Retardo antes de decidir, para simular a un usuario pensándoselo.</summary>
    public TimeSpan AskDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Si se establece, <see cref="OpenFileForWritingAsync"/> lanza esta excepción.</summary>
    public Exception? OpenFileFailure { get; set; }

    public List<IncomingTransferRequest> AskedRequests { get; } = [];

    public List<string> RequestedFileNames { get; } = [];

    public IReadOnlyList<ReceivedFile>? CompletedFiles { get; private set; }

    public Exception? FailureReported { get; private set; }

    /// <summary>Contenido de los ficheros recibidos, por nombre saneado.</summary>
    public IReadOnlyDictionary<string, byte[]> ReceivedContent =>
        _files.ToDictionary(p => p.Key, p => p.Value.ToArray());

    public async Task<bool> ShouldAcceptAsync(
        IncomingTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        AskedRequests.Add(request);

        if (AskDelay > TimeSpan.Zero)
        {
            await Task.Delay(AskDelay, cancellationToken);
        }

        return Accept;
    }

    public Task<Stream> OpenFileForWritingAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        RequestedFileNames.Add(fileName);

        if (OpenFileFailure is not null)
        {
            return Task.FromException<Stream>(OpenFileFailure);
        }

        // El stream no se cierra de verdad para poder inspeccionar el contenido después.
        var stream = new NonClosingMemoryStream();
        _files[fileName] = stream;
        return Task.FromResult<Stream>(stream);
    }

    public Task OnTransferCompletedAsync(
        IReadOnlyList<ReceivedFile> files,
        CancellationToken cancellationToken = default)
    {
        CompletedFiles = files;
        return Task.CompletedTask;
    }

    public Task OnTransferFailedAsync(Exception error, CancellationToken cancellationToken = default)
    {
        FailureReported = error;
        return Task.CompletedTask;
    }

    /// <summary>MemoryStream que ignora el cierre, para poder leerlo tras la transferencia.</summary>
    private sealed class NonClosingMemoryStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            // Deliberadamente vacío.
        }
    }
}
