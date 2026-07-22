using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AirDrop.Core.Archives;
using AirDrop.Core.Protocol;
using AirDrop.Core.Protocol.Messages;
using AirDrop.Core.Protocol.Plist;
using AirDrop.Server.Security;
using Xunit;

namespace AirDrop.Server.Tests;

/// <summary>
/// Tests de extremo a extremo del receptor: servidor real, TLS real, HTTP real.
/// </summary>
/// <remarks>
/// Se prueba contra el servidor levantado de verdad en vez de con dobles, porque lo que interesa
/// verificar es justamente el comportamiento del protocolo sobre el cable: el plist binario, el
/// gzip sin cabecera y el estado compartido por conexión TLS.
/// </remarks>
public sealed class AirDropServerTests : IAsyncLifetime
{
    private const int RegularFile644 = 0x81A4;

    private RecordingTransferHandler _handler = null!;
    private AirDropServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _handler = new RecordingTransferHandler();

        _server = new AirDropServer(
            new AirDropServerOptions
            {
                // Puerto 0: el sistema asigna uno libre y así los tests no chocan entre sí
                // ni con un AirDrop real que estuviera escuchando en el 8770.
                Port = 0,
                Addresses = [IPAddress.Loopback],
                Certificate = AirDropCertificate.CreateSelfSigned(),
            },
            _handler);

        await _server.StartAsync();

        // El TLS de AirDrop usa certificados autofirmados sin verificación de peer, así que un
        // cliente que los acepte reproduce el comportamiento real (docs/01 §6.1).
        var socketsHandler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        _client = new HttpClient(socketsHandler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_server.Port}/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Discover_ReturnsOurIdentity()
    {
        // Esta respuesta es lo que hace que aparezcamos con nombre en la hoja del emisor.
        var response = await PostPlistAsync("/Discover", new DiscoverRequest(null).ToPlist());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = DiscoverResponse.FromPlist(await ReadPlistAsync(response));
        Assert.Equal("PC de pruebas", result.ReceiverComputerName);
        Assert.Equal("Windows11,1", result.ReceiverModelName);
    }

    [Fact]
    public async Task Discover_AcceptsAnEmptyBody()
    {
        // Hay emisores que hacen /Discover sin contenido.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Discover")
        {
            Content = new ByteArrayContent([]),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discover_RejectsAMalformedPlist()
    {
        using var content = new ByteArrayContent(Encoding.ASCII.GetBytes("esto no es un plist"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _client.PostAsync("/Discover", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ask_WhenAcceptedReturnsOurIdentity()
    {
        _handler.Accept = true;

        var response = await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = AskResponse.FromPlist(await ReadPlistAsync(response));
        Assert.Equal("PC de pruebas", result.ReceiverComputerName);

        var asked = Assert.Single(_handler.AskedRequests);
        Assert.Equal("iPhone de Álvaro", asked.Request.SenderComputerName);
        Assert.Equal(2, asked.Request.Files.Count);
    }

    [Fact]
    public async Task Ask_WhenRejectedReturnsUnauthorized()
    {
        _handler.Accept = false;

        var response = await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ask_SurvivesALongUserDecision()
    {
        // El emisor mantiene la petición abierta mientras el usuario decide, que pueden ser
        // minutos. Los timeouts por defecto de Kestrel la cortarían antes de tiempo.
        _handler.AskDelay = TimeSpan.FromSeconds(3);
        _handler.Accept = true;

        var response = await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutAnAcceptedAskIsRejected()
    {
        // Medida de seguridad central: sin esto, cualquiera podría escribir ficheros en el equipo
        // sin que el usuario hubiera aceptado nada.
        var archive = await CreateArchiveAsync(new Dictionary<string, byte[]>
        {
            ["./intruso.txt"] = "no debería llegar"u8.ToArray(),
        });

        var response = await PostArchiveAsync(archive);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_handler.RequestedFileNames);
    }

    [Fact]
    public async Task Upload_AfterAcceptedAskExtractsTheFiles()
    {
        _handler.Accept = true;

        var files = new Dictionary<string, byte[]>
        {
            ["./IMG_0001.HEIC"] = RandomBytes(2048),
            ["./documento.pdf"] = RandomBytes(37),
            ["./carpeta/anidado.txt"] = "contenido anidado"u8.ToArray(),
        };

        var askResponse = await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());
        Assert.Equal(HttpStatusCode.OK, askResponse.StatusCode);

        var uploadResponse = await PostArchiveAsync(await CreateArchiveAsync(files));

        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        var received = _handler.ReceivedContent;
        Assert.Equal(3, received.Count);
        Assert.Equal(files["./IMG_0001.HEIC"], received["IMG_0001.HEIC"]);
        Assert.Equal(files["./documento.pdf"], received["documento.pdf"]);
        // La ruta relativa se conserva, con los separadores del sistema.
        Assert.Equal(
            files["./carpeta/anidado.txt"],
            received[Path.Combine("carpeta", "anidado.txt")]);

        Assert.NotNull(_handler.CompletedFiles);
        Assert.Equal(3, _handler.CompletedFiles.Count);
    }

    [Fact]
    public async Task Upload_AcceptsAnUncompressedArchive()
    {
        // El gzip se detecta por el magic, no por las cabeceras, así que un emisor que no
        // comprima debe funcionar igual.
        _handler.Accept = true;
        await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        var archive = await CreateArchiveAsync(
            new Dictionary<string, byte[]> { ["./plano.txt"] = "sin comprimir"u8.ToArray() },
            compress: false);

        var response = await PostArchiveAsync(archive);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sin comprimir"u8.ToArray(), _handler.ReceivedContent["plano.txt"]);
    }

    [Theory]
    [InlineData("../../../Windows/System32/malicioso.dll")]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\malicioso.dll")]
    public async Task Upload_RejectsPathTraversalAttempts(string maliciousName)
    {
        // El nombre lo elige íntegramente el emisor, que no es de confianza.
        _handler.Accept = true;
        await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        var archive = await CreateArchiveAsync(
            new Dictionary<string, byte[]> { [maliciousName] = "carga"u8.ToArray() });

        var response = await PostArchiveAsync(archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Y sobre todo: no se llegó a abrir ningún fichero para escritura.
        Assert.Empty(_handler.RequestedFileNames);
        Assert.NotNull(_handler.FailureReported);
    }

    [Fact]
    public async Task Upload_RejectsAMalformedArchive()
    {
        _handler.Accept = true;
        await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        var response = await PostArchiveAsync(Encoding.ASCII.GetBytes("esto no es un CPIO"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(_handler.FailureReported);
    }

    [Fact]
    public async Task Upload_SkipsSymbolicLinks()
    {
        // Su destino podría apuntar fuera del directorio de descargas.
        _handler.Accept = true;
        await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        var buffer = new MemoryStream();
        await using (var writer = new CpioWriter(buffer, CpioFormat.Odc, leaveOpen: true))
        {
            const int symlinkMode = 0xA1FF;
            var target = "/etc/passwd"u8.ToArray();
            await writer.WriteEntryAsync(
                new CpioEntry("./enlace", symlinkMode, target.Length, DateTimeOffset.UnixEpoch),
                new MemoryStream(target));
            await writer.WriteEntryAsync(
                new CpioEntry("./normal.txt", RegularFile644, 2, DateTimeOffset.UnixEpoch),
                new MemoryStream("ok"u8.ToArray()));
        }

        var response = await PostArchiveAsync(Compress(buffer.ToArray()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["normal.txt"], _handler.RequestedFileNames);
    }

    [Fact]
    public async Task Upload_CannotBeReplayedOnTheSameConnection()
    {
        // La autorización se consume: un segundo /Upload necesita un /Ask nuevo.
        _handler.Accept = true;
        await PostPlistAsync("/Ask", CreateAskRequest().ToPlist());

        var archive = await CreateArchiveAsync(
            new Dictionary<string, byte[]> { ["./uno.txt"] = "x"u8.ToArray() });

        Assert.Equal(HttpStatusCode.OK, (await PostArchiveAsync(archive)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostArchiveAsync(archive)).StatusCode);
    }

    [Fact]
    public async Task UnknownEndpointsReturnNotFound()
    {
        var response = await PostPlistAsync("/Exchange", new PlistDictionary());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static AskRequest CreateAskRequest() =>
        new(
            "iPhone de Álvaro",
            "iPhone17,1",
            [
                AirDropFileMetadata.ForFile("IMG_0001.HEIC", UniformTypeIdentifiers.Heic),
                AirDropFileMetadata.ForFile("documento.pdf", UniformTypeIdentifiers.Pdf),
            ],
            BundleId: "com.apple.finder");

    private async Task<HttpResponseMessage> PostPlistAsync(string path, PlistDictionary plist)
    {
        using var content = new ByteArrayContent(BinaryPlistWriter.Write(plist));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(path, content);
    }

    private async Task<HttpResponseMessage> PostArchiveAsync(byte[] archive)
    {
        using var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync("/Upload", content);
    }

    private static async Task<PlistDictionary> ReadPlistAsync(HttpResponseMessage response) =>
        BinaryPlistReader.ReadDictionary(await response.Content.ReadAsByteArrayAsync());

    private static async Task<byte[]> CreateArchiveAsync(
        IReadOnlyDictionary<string, byte[]> files,
        bool compress = true)
    {
        var buffer = new MemoryStream();
        await using (var writer = new CpioWriter(buffer, CpioFormat.Odc, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                await writer.WriteEntryAsync(
                    new CpioEntry(name, RegularFile644, content.Length, DateTimeOffset.UnixEpoch),
                    new MemoryStream(content));
            }
        }

        return compress ? Compress(buffer.ToArray()) : buffer.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return compressed.ToArray();
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(Seed: count).NextBytes(bytes);
        return bytes;
    }
}
