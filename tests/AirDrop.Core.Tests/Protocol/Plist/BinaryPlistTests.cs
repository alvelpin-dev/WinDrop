using System.Text;
using AirDrop.Core.Protocol.Plist;
using Xunit;

namespace AirDrop.Core.Tests.Protocol.Plist;

/// <summary>
/// Tests del lector y el escritor de binary property lists.
/// </summary>
/// <remarks>
/// Los tests de round-trip prueban que somos autoconsistentes. Los de vectores fijos prueban que
/// producimos exactamente el formato de Apple, que es lo que de verdad importa: un iPhone no va a
/// leer nuestro plist con nuestro lector.
/// </remarks>
public class BinaryPlistTests
{
    [Fact]
    public void Read_RejectsContentWithoutMagicHeader()
    {
        var notAPlist = new byte[64];

        var exception = Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Read(notAPlist));

        Assert.Contains("bplist00", exception.Message);
    }

    [Fact]
    public void Read_RejectsTruncatedContent()
    {
        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Read("bplist00"u8.ToArray()));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65_535L)]
    [InlineData(65_536L)]
    [InlineData(uint.MaxValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void RoundTrip_PreservesIntegersAcrossAllWidths(long value)
    {
        AssertRoundTrip(new PlistInteger(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AirDrop")]
    [InlineData("SenderComputerName")]
    // Cadenas no ASCII: fuerzan la ruta UTF-16BE. Un nombre de equipo con acentos es lo normal.
    [InlineData("PC de Álvaro")]
    [InlineData("iPhone 📱 de prueba")]
    // Longitud >= 15: fuerza el marcador extendido con la longitud en un entero embebido.
    [InlineData("una cadena deliberadamente larga para desbordar el nibble de longitud")]
    public void RoundTrip_PreservesStrings(string value)
    {
        AssertRoundTrip(new PlistString(value));
    }

    [Fact]
    public void RoundTrip_PreservesBooleans()
    {
        AssertRoundTrip(new PlistBoolean(true));
        AssertRoundTrip(new PlistBoolean(false));
    }

    [Fact]
    public void RoundTrip_PreservesReals()
    {
        AssertRoundTrip(new PlistReal(3.14159));
        AssertRoundTrip(new PlistReal(0));
        AssertRoundTrip(new PlistReal(-1.5e300));
    }

    [Fact]
    public void RoundTrip_PreservesData()
    {
        AssertRoundTrip(new PlistData([]));
        AssertRoundTrip(new PlistData([0x00, 0xFF, 0x42]));
        // Un SenderRecordData real ronda los pocos KB: hay que cruzar el umbral del nibble.
        AssertRoundTrip(new PlistData([.. Enumerable.Range(0, 5000).Select(i => (byte)i)]));
    }

    [Fact]
    public void RoundTrip_PreservesDatesWithinMillisecondPrecision()
    {
        var original = new PlistDate(new DateTimeOffset(2026, 7, 22, 13, 45, 30, TimeSpan.Zero));

        var result = (PlistDate)WriteThenRead(original);

        Assert.Equal(original.Value, result.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void PlistDate_UsesAppleReferenceEpochNotUnixEpoch()
    {
        // La época de Core Foundation es 2001-01-01. Confundirla con la de Unix desplaza
        // cualquier fecha 31 años.
        var epoch = PlistDate.FromAppleSeconds(0);

        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), epoch.Value);
        Assert.Equal(0, epoch.ToAppleSeconds());
    }

    [Fact]
    public void RoundTrip_PreservesNestedStructures()
    {
        // Aproximación a la forma real de un /Ask: un diccionario con un array de diccionarios.
        var ask = new PlistDictionaryBuilder()
            .Set("SenderComputerName", "PC de pruebas")
            .Set("SenderModelName", "Windows11,1")
            .Set("ConvertMediaFormats", false)
            .Set("Files", new PlistArray(
                new PlistDictionaryBuilder()
                    .Set("FileName", "IMG_0001.HEIC")
                    .Set("FileType", "public.heic")
                    .Set("FileBomPath", "./IMG_0001.HEIC")
                    .Set("FileIsDirectory", false)
                    .Build(),
                new PlistDictionaryBuilder()
                    .Set("FileName", "documento.pdf")
                    .Set("FileType", "com.adobe.pdf")
                    .Set("FileBomPath", "./documento.pdf")
                    .Set("FileIsDirectory", false)
                    .Build()))
            .Build();

        var result = (PlistDictionary)WriteThenRead(ask);

        Assert.Equal("PC de pruebas", result.GetString("SenderComputerName"));
        Assert.False(result.GetBoolean("ConvertMediaFormats"));

        var files = result.GetArray("Files");
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);
        Assert.Equal("public.heic", ((PlistDictionary)files[0]).GetString("FileType"));
        Assert.Equal("documento.pdf", ((PlistDictionary)files[1]).GetString("FileName"));
    }

    [Fact]
    public void RoundTrip_PreservesEmptyContainers()
    {
        AssertRoundTrip(new PlistArray());
        AssertRoundTrip(new PlistDictionary());
    }

    [Fact]
    public void RoundTrip_PreservesLargeContainersBeyondNibbleLimit()
    {
        // Más de 15 elementos: obliga a usar el marcador de longitud extendida en el contenedor.
        var array = new PlistArray([.. Enumerable.Range(0, 300).Select(PlistValue (i) => new PlistInteger(i))]);

        var result = (PlistArray)WriteThenRead(array);

        Assert.Equal(300, result.Count);
        Assert.Equal(299L, ((PlistInteger)result[299]).Value);
    }

    [Fact]
    public void Write_DeduplicatesRepeatedScalars()
    {
        // Las claves y valores repetidos deben compartir objeto. Sin deduplicación, los mensajes
        // con muchos ficheros crecen de forma innecesaria.
        var withRepetition = new PlistArray(
            new PlistString("public.jpeg"),
            new PlistString("public.jpeg"),
            new PlistString("public.jpeg"),
            new PlistString("public.jpeg"));

        var singleValue = new PlistArray(new PlistString("public.jpeg"));

        var repeated = BinaryPlistWriter.Write(withRepetition);
        var single = BinaryPlistWriter.Write(singleValue);

        // La diferencia debe ser solo la de las referencias extra, no la de cuatro cadenas.
        Assert.True(
            repeated.Length - single.Length < "public.jpeg".Length,
            $"Sin deduplicar: {single.Length} -> {repeated.Length} bytes.");
    }

    [Fact]
    public void Write_ProducesHeaderAndTrailerInAppleFormat()
    {
        var data = BinaryPlistWriter.Write(new PlistString("x"));

        Assert.Equal("bplist00", Encoding.ASCII.GetString(data[..8]));

        // El trailer ocupa los últimos 32 bytes y declara la raíz en el objeto 0.
        var trailer = data.AsSpan()[^32..];
        Assert.Equal(0, trailer[..6].ToArray().Sum(b => b));   // 5 sin usar + sortVersion
        Assert.True(trailer[6] is >= 1 and <= 8);              // offsetIntSize
        Assert.True(trailer[7] is >= 1 and <= 8);              // objectRefSize
        Assert.Equal(0, trailer[16..24].ToArray().Sum(b => b)); // topObject == 0
    }

    [Fact]
    public void ReadDictionary_ThrowsWhenRootIsNotADictionary()
    {
        var arrayAtRoot = BinaryPlistWriter.Write(new PlistArray(new PlistString("x")));

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.ReadDictionary(arrayAtRoot));
    }

    [Fact]
    public void Read_RejectsOffsetTablePointingOutsideTheBuffer()
    {
        // Un plist manipulado no debe poder hacernos leer fuera del buffer.
        var data = BinaryPlistWriter.Write(new PlistString("x"));
        data[^1] = 0xFF;   // corrompe el byte bajo de offsetTableOffset

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Read(data));
    }

    [Fact]
    public void Read_RejectsDeclaredLengthLargerThanTheBuffer()
    {
        var data = BinaryPlistWriter.Write(new PlistData([.. Enumerable.Repeat((byte)0x41, 100)]));

        // El objeto 0 empieza en el offset 8: marcador 0x4F (datos con longitud extendida) seguido
        // de un entero de 1 byte con la longitud. Se infla a 0xFF sin aportar los datos.
        Assert.Equal(0x4F, data[8]);
        Assert.Equal(0x10, data[9]);   // entero de 1 byte
        data[10] = 0xFF;

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Read(data));
    }

    private static PlistValue WriteThenRead(PlistValue value) =>
        BinaryPlistReader.Read(BinaryPlistWriter.Write(value));

    private static void AssertRoundTrip(PlistValue value) =>
        Assert.Equal(value, WriteThenRead(value));
}
