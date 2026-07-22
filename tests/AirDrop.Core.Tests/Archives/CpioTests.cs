using System.Text;
using AirDrop.Core.Archives;
using Xunit;

namespace AirDrop.Core.Tests.Archives;

/// <summary>
/// Tests del lector y el escritor de CPIO.
/// </summary>
/// <remarks>
/// Los vectores construidos a mano son los que de verdad valen: comprueban que interpretamos el
/// formato tal y como lo define POSIX, no solo que nuestro lector entiende a nuestro escritor.
/// Un iPhone no va a usar nuestro escritor.
/// </remarks>
public class CpioTests
{
    /// <summary>Modo POSIX de un fichero regular con permisos 0644.</summary>
    private const int RegularFile644 = 0x81A4;

    [Fact]
    public async Task Reader_ParsesHandBuiltOdcArchive()
    {
        // Archivo odc construido byte a byte según la especificación POSIX.
        var archive = new MemoryStream(BuildOdcArchive("hola.txt", "Hola"));

        await using var reader = new CpioReader(archive);

        var entry = await reader.MoveNextAsync();

        Assert.NotNull(entry);
        Assert.Equal("hola.txt", entry.Name);
        Assert.Equal(4, entry.DataLength);
        Assert.True(entry.IsRegularFile);
        Assert.False(entry.IsDirectory);
        Assert.Equal(CpioFormat.Odc, reader.DetectedFormat);

        var content = new MemoryStream();
        await reader.CopyEntryDataToAsync(content);
        Assert.Equal("Hola", Encoding.UTF8.GetString(content.ToArray()));

        Assert.Null(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Reader_RejectsUnknownMagic()
    {
        var garbage = new MemoryStream(Encoding.ASCII.GetBytes(new string('9', 200)));

        await using var reader = new CpioReader(garbage);

        var exception = await Assert.ThrowsAsync<CpioFormatException>(
            async () => await reader.MoveNextAsync());

        Assert.Contains("999999", exception.Message);
    }

    [Fact]
    public async Task Reader_RejectsArchiveWithoutTrailer()
    {
        // Un archivo que termina tras la última entrada, sin TRAILER!!!, está truncado.
        var withoutTrailer = BuildOdcArchive("a.txt", "x", includeTrailer: false);

        await using var reader = new CpioReader(new MemoryStream(withoutTrailer));

        await reader.MoveNextAsync();

        var exception = await Assert.ThrowsAsync<CpioFormatException>(
            async () => await reader.MoveNextAsync());

        Assert.Contains("TRAILER", exception.Message);
    }

    [Fact]
    public async Task Reader_DetectsTruncatedFileData()
    {
        // La cabecera declara 100 bytes pero solo hay 4: el receptor debe fallar, no aceptar
        // un fichero incompleto en silencio.
        var archive = BuildOdcArchive("mentiroso.bin", "Hola", declaredSizeOverride: 100);

        await using var reader = new CpioReader(new MemoryStream(archive));
        await reader.MoveNextAsync();

        await Assert.ThrowsAsync<CpioFormatException>(
            async () => await reader.CopyEntryDataToAsync(Stream.Null));
    }

    [Theory]
    [InlineData(CpioFormat.Odc)]
    [InlineData(CpioFormat.Newc)]
    public async Task RoundTrip_PreservesMultipleEntries(CpioFormat format)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["./IMG_0001.HEIC"] = RandomBytes(1024),
            ["./documento.pdf"] = RandomBytes(37),         // tamaño no alineado a 4
            ["./vídeo con acentos y 📱.mp4"] = RandomBytes(4096),
            ["./vacío.txt"] = [],
        };

        var buffer = new MemoryStream();
        await using (var writer = new CpioWriter(buffer, format, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new CpioEntry(name, RegularFile644, content.Length, DateTimeOffset.UnixEpoch);
                await writer.WriteEntryAsync(entry, new MemoryStream(content));
            }
        }

        buffer.Position = 0;
        var recovered = new Dictionary<string, byte[]>();
        await using (var reader = new CpioReader(buffer, leaveOpen: true))
        {
            while (await reader.MoveNextAsync() is { } entry)
            {
                var content = new MemoryStream();
                await reader.CopyEntryDataToAsync(content);
                recovered[entry.Name] = content.ToArray();
            }
        }

        Assert.Equal(files.Count, recovered.Count);
        foreach (var (name, expected) in files)
        {
            Assert.True(recovered.ContainsKey(name), $"Falta la entrada '{name}'.");
            Assert.Equal(expected, recovered[name]);
        }
    }

    [Fact]
    public async Task Reader_SkipsUnreadDataWhenAdvancing()
    {
        // El consumidor puede decidir no leer una entrada; avanzar debe seguir funcionando.
        var buffer = new MemoryStream();
        await using (var writer = new CpioWriter(buffer, CpioFormat.Odc, leaveOpen: true))
        {
            await writer.WriteEntryAsync(
                new CpioEntry("saltado.bin", RegularFile644, 5000, DateTimeOffset.UnixEpoch),
                new MemoryStream(RandomBytes(5000)));
            await writer.WriteEntryAsync(
                new CpioEntry("leido.txt", RegularFile644, 2, DateTimeOffset.UnixEpoch),
                new MemoryStream("ok"u8.ToArray()));
        }

        buffer.Position = 0;
        await using var reader = new CpioReader(buffer);

        Assert.Equal("saltado.bin", (await reader.MoveNextAsync())!.Name);
        // Se avanza sin haber leído los 5000 bytes.
        Assert.Equal("leido.txt", (await reader.MoveNextAsync())!.Name);

        var content = new MemoryStream();
        await reader.CopyEntryDataToAsync(content);
        Assert.Equal("ok", Encoding.UTF8.GetString(content.ToArray()));
    }

    [Fact]
    public async Task Writer_AlwaysEmitsTrailerOnDispose()
    {
        var buffer = new MemoryStream();

        await using (var writer = new CpioWriter(buffer, CpioFormat.Odc, leaveOpen: true))
        {
            await writer.WriteEntryAsync(
                new CpioEntry("a.txt", RegularFile644, 1, DateTimeOffset.UnixEpoch),
                new MemoryStream("x"u8.ToArray()));
        }

        Assert.Contains(CpioEntry.TrailerName, Encoding.ASCII.GetString(buffer.ToArray()));
    }

    [Fact]
    public async Task Writer_FailsWhenSourceIsShorterThanDeclaredLength()
    {
        var buffer = new MemoryStream();
        await using var writer = new CpioWriter(buffer, CpioFormat.Odc, leaveOpen: true);

        var entry = new CpioEntry("mentiroso.bin", RegularFile644, 1000, DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<CpioFormatException>(
            async () => await writer.WriteEntryAsync(entry, new MemoryStream(RandomBytes(10))));
    }

    [Fact]
    public void CpioEntry_ClassifiesPosixFileModes()
    {
        Assert.True(new CpioEntry("d", 0x41ED, 0, DateTimeOffset.UnixEpoch).IsDirectory);
        Assert.True(new CpioEntry("f", RegularFile644, 0, DateTimeOffset.UnixEpoch).IsRegularFile);
        Assert.True(new CpioEntry("l", 0xA1FF, 0, DateTimeOffset.UnixEpoch).IsSymbolicLink);
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(Seed: count).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Construye un archivo CPIO en formato odc byte a byte, sin usar <see cref="CpioWriter"/>,
    /// para poder validar el lector de forma independiente.
    /// </summary>
    private static byte[] BuildOdcArchive(
        string name,
        string content,
        bool includeTrailer = true,
        long? declaredSizeOverride = null)
    {
        var buffer = new MemoryStream();
        AppendOdcEntry(buffer, name, Encoding.UTF8.GetBytes(content), RegularFile644, declaredSizeOverride);

        if (includeTrailer)
        {
            AppendOdcEntry(buffer, CpioEntry.TrailerName, [], mode: 0, declaredSizeOverride: null);
        }

        return buffer.ToArray();
    }

    private static void AppendOdcEntry(
        Stream target,
        string name,
        byte[] content,
        int mode,
        long? declaredSizeOverride)
    {
        static string Octal(long value, int width) =>
            Convert.ToString(value, 8).PadLeft(width, '0');

        var nameSize = Encoding.UTF8.GetByteCount(name) + 1;
        var declaredSize = declaredSizeOverride ?? content.Length;

        var header = string.Concat(
            "070707",              // magic
            Octal(0, 6),           // c_dev
            Octal(0, 6),           // c_ino
            Octal(mode, 6),        // c_mode
            Octal(0, 6),           // c_uid
            Octal(0, 6),           // c_gid
            Octal(1, 6),           // c_nlink
            Octal(0, 6),           // c_rdev
            Octal(0, 11),          // c_mtime
            Octal(nameSize, 6),    // c_namesize
            Octal(declaredSize, 11)); // c_filesize

        Assert.Equal(76, header.Length);

        target.Write(Encoding.ASCII.GetBytes(header));
        target.Write(Encoding.UTF8.GetBytes(name));
        target.WriteByte(0);
        target.Write(content);
    }
}
