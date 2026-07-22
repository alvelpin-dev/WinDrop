using System.Net;
using AirDrop.Client;
using AirDrop.Server;
using AirDrop.Server.Security;
using Xunit;

namespace AirDrop.Integration.Tests;

/// <summary>
/// Transferencia completa entre el emisor y el receptor de este proyecto.
/// </summary>
/// <remarks>
/// Es el test más importante de la suite: recorre el protocolo entero —<c>/Discover</c>,
/// <c>/Ask</c>, <c>/Upload</c>— sobre TLS real, con el archivo CPIO comprimido de verdad. Cubre
/// exactamente el escenario Windows ↔ Windows que la aplicación soporta hoy.
/// </remarks>
public sealed class EndToEndTransferTests : IAsyncLifetime
{
    private readonly string _workDirectory = Path.Combine(
        Path.GetTempPath(), $"windrop-e2e-{Guid.NewGuid():N}");

    private CapturingHandler _receiverHandler = null!;
    private AirDropServer _server = null!;
    private AirDropClient _client = null!;
    private IPEndPoint _endpoint = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDirectory);

        _receiverHandler = new CapturingHandler(Path.Combine(_workDirectory, "recibidos"));

        _server = new AirDropServer(
            new AirDropServerOptions
            {
                Port = 0,
                Addresses = [IPAddress.Loopback],
                Certificate = AirDropCertificate.CreateSelfSigned(),
            },
            _receiverHandler);

        await _server.StartAsync();

        _endpoint = new IPEndPoint(IPAddress.Loopback, _server.Port);
        _client = new AirDropClient(new SenderIdentity("PC emisor"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();

        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza best-effort: no debe hacer fallar el test.
        }
    }

    [Fact]
    public async Task Discover_ResolvesTheReceiverName()
    {
        // Es lo que convierte un identificador mDNS opaco en un nombre legible para la interfaz.
        var identity = await _client.DiscoverAsync(_endpoint);

        Assert.NotNull(identity);
        Assert.Equal("PC receptor", identity.ReceiverComputerName);
    }

    [Fact]
    public async Task Transfer_DeliversFilesIntactWithProgress()
    {
        var files = new[]
        {
            CreateFile("foto.jpg", 512 * 1024),
            CreateFile("documento.pdf", 4096),
            CreateFile("nota con acentos y 📱.txt", 128),
        };

        var updates = new List<TransferProgress>();
        var progress = new Progress<TransferProgress>(p =>
        {
            lock (updates)
            {
                updates.Add(p);
            }
        });

        var result = await _client.SendAsync(_endpoint, files, progress);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal("PC receptor", result.ReceiverName);

        // Los ficheros llegan con el contenido intacto.
        foreach (var file in files)
        {
            var destination = Path.Combine(_receiverHandler.Directory, file.Name);
            Assert.True(File.Exists(destination), $"No llegó '{file.Name}'.");
            Assert.Equal(File.ReadAllBytes(file.Path), File.ReadAllBytes(destination));
        }

        // Y el progreso recorre las fases esperadas.
        lock (updates)
        {
            Assert.Contains(updates, u => u.Phase == TransferPhase.Discovering);
            Assert.Contains(updates, u => u.Phase == TransferPhase.WaitingForAcceptance);
            Assert.Contains(updates, u => u.Phase == TransferPhase.Uploading);
            Assert.Contains(updates, u => u.Phase == TransferPhase.Completed);

            // Y avanza de verdad, en lugar de saltar de 0 a 100.
            var uploading = updates.Where(u => u.Phase == TransferPhase.Uploading).ToList();
            Assert.True(uploading.Count > 1, "El progreso de subida no se reportó por partes.");
            Assert.Contains(uploading, u => u.Fraction is > 0 and < 1);
        }
    }

    [Fact]
    public async Task Transfer_WhenReceiverRejectsReportsRejection()
    {
        _receiverHandler.Accept = false;

        var result = await _client.SendAsync(_endpoint, [CreateFile("no-deseado.txt", 64)]);

        Assert.Equal(TransferPhase.Rejected, result.Phase);
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(_receiverHandler.Directory)
            && Directory.EnumerateFiles(_receiverHandler.Directory).Any());
    }

    [Fact]
    public async Task Transfer_CanBeCancelledWhileWaitingForAcceptance()
    {
        // El caso real: el usuario del receptor no contesta y el emisor se cansa.
        _receiverHandler.AskDelay = TimeSpan.FromMinutes(1);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var result = await _client.SendAsync(
            _endpoint, [CreateFile("lento.txt", 64)], progress: null, cancellation.Token);

        Assert.Equal(TransferPhase.Cancelled, result.Phase);
    }

    [Fact]
    public async Task Transfer_HandlesLargeFilesWithoutBufferingThemInMemory()
    {
        // 32 MB no prueban el límite de memoria por sí solos, pero sí que el camino en streaming
        // funciona de extremo a extremo con un fichero que no cabe en un buffer típico.
        var file = CreateFile("video.mp4", 32 * 1024 * 1024);

        var result = await _client.SendAsync(_endpoint, [file]);

        Assert.True(result.Succeeded, result.Error?.ToString());

        var destination = Path.Combine(_receiverHandler.Directory, file.Name);
        Assert.Equal(new FileInfo(file.Path).Length, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task Transfer_AssignsCorrectUniformTypeIdentifiers()
    {
        // El UTI decide si el receptor guarda el fichero como foto o como documento.
        await _client.SendAsync(_endpoint,
        [
            CreateFile("imagen.heic", 128),
            CreateFile("hoja.xlsx", 128),
        ]);

        var ask = Assert.Single(_receiverHandler.AskedRequests);
        Assert.Equal("public.heic", ask.Files.First(f => f.FileName == "imagen.heic").FileType);
        Assert.Equal(
            "org.openxmlformats.spreadsheetml.sheet",
            ask.Files.First(f => f.FileName == "hoja.xlsx").FileType);
    }

    [Fact]
    public async Task Transfer_SendsSenderIdentityToTheReceiver()
    {
        await _client.SendAsync(_endpoint, [CreateFile("x.txt", 16)]);

        var ask = Assert.Single(_receiverHandler.AskedRequests);
        Assert.Equal("PC emisor", ask.SenderComputerName);
        Assert.Equal("Windows11,1", ask.SenderModelName);
    }

    private FileToSend CreateFile(string name, int size)
    {
        var path = Path.Combine(_workDirectory, name);
        var content = new byte[size];
        new Random(Seed: size).NextBytes(content);
        File.WriteAllBytes(path, content);
        return new FileToSend(path, name);
    }

    /// <summary>Receptor que escribe en disco y registra lo ocurrido.</summary>
    private sealed class CapturingHandler(string directory) : IIncomingTransferHandler
    {
        public string Directory { get; } = directory;

        public ReceiverIdentity Identity { get; } = new("PC receptor");

        public bool Accept { get; set; } = true;

        public TimeSpan AskDelay { get; set; } = TimeSpan.Zero;

        public List<Core.Protocol.Messages.AskRequest> AskedRequests { get; } = [];

        public async Task<bool> ShouldAcceptAsync(
            IncomingTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            AskedRequests.Add(request.Request);

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
            var destination = Core.Storage.SafeRelativePath.CombineWithin(Directory, fileName);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            return Task.FromResult<Stream>(File.Create(destination));
        }

        public Task OnTransferCompletedAsync(
            IReadOnlyList<ReceivedFile> files,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task OnTransferFailedAsync(
            Exception error,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
