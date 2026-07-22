using AirDrop.Core.Storage;
using Xunit;

namespace AirDrop.Core.Tests.Storage;

/// <summary>
/// Tests de la frontera de seguridad del saneado de rutas.
/// </summary>
/// <remarks>
/// Los nombres de fichero de una transferencia los elige íntegramente el emisor, que es un
/// dispositivo no confiable de la red local. Un fallo aquí es escritura arbitraria de ficheros,
/// así que estos tests son los más importantes del proyecto.
/// </remarks>
public class SafeRelativePathTests
{
    [Theory]
    [InlineData("IMG_0001.HEIC", "IMG_0001.HEIC")]
    [InlineData("./IMG_0001.HEIC", "IMG_0001.HEIC")]     // prefijo habitual de los CPIO de AirDrop
    [InlineData("./fotos/IMG_0001.HEIC", "fotos\\IMG_0001.HEIC")]
    [InlineData("carpeta/sub/fichero.txt", "carpeta\\sub\\fichero.txt")]
    [InlineData("./a/./b/fichero.txt", "a\\b\\fichero.txt")]
    [InlineData("vídeo con acentos y 📱.mp4", "vídeo con acentos y 📱.mp4")]
    public void Normalize_AcceptsLegitimatePaths(string input, string expected)
    {
        Assert.Equal(expected, SafeRelativePath.Normalize(input));
    }

    [Theory]
    // Escapes clásicos por travesía de directorios.
    [InlineData("../fichero.txt")]
    [InlineData("../../Windows/System32/algo.dll")]
    [InlineData("./../../fichero.txt")]
    [InlineData("carpeta/../../fichero.txt")]
    [InlineData("a/b/c/../../../../fichero.txt")]
    // Con barra invertida: en Windows también separa, y un origen POSIX puede generarlas.
    [InlineData("..\\fichero.txt")]
    [InlineData("carpeta\\..\\..\\fichero.txt")]
    public void Normalize_RejectsDirectoryTraversal(string input)
    {
        var exception = Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));

        Assert.Contains("salir del destino", exception.Message);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/fichero.txt")]
    [InlineData("\\fichero.txt")]
    [InlineData("C:/Windows/System32/algo.dll")]
    [InlineData("C:\\Windows\\algo.dll")]
    [InlineData("C:algo.txt")]
    public void Normalize_RejectsAbsolutePaths(string input)
    {
        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("PRN")]
    // Un nombre reservado sigue siéndolo con extensión: "CON.txt" es la consola.
    [InlineData("CON.txt")]
    [InlineData("NUL.jpg")]
    [InlineData("carpeta/CON.txt")]
    public void Normalize_RejectsWindowsReservedNames(string input)
    {
        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));
    }

    [Theory]
    [InlineData("fichero\0.txt")]     // el nulo trunca cadenas en las APIs nativas
    [InlineData("fichero\r\n.txt")]
    [InlineData("fichero\t.txt")]
    public void Normalize_RejectsControlCharacters(string input)
    {
        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));
    }

    [Theory]
    [InlineData("fichero<.txt")]
    [InlineData("fichero>.txt")]
    [InlineData("fichero:alterno.txt")]   // flujo de datos alternativo NTFS
    [InlineData("fichero\".txt")]
    [InlineData("fichero|.txt")]
    [InlineData("fichero?.txt")]
    [InlineData("fichero*.txt")]
    public void Normalize_RejectsCharactersInvalidOnWindows(string input)
    {
        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));
    }

    [Theory]
    [InlineData("fichero.txt.", "fichero.txt")]
    [InlineData("fichero.txt   ", "fichero.txt")]
    [InlineData("fichero.txt . . ", "fichero.txt")]
    public void Normalize_StripsTrailingDotsAndSpaces(string input, string expected)
    {
        // Windows ignora puntos y espacios finales al resolver rutas, así que "algo." y "algo"
        // son el mismo fichero. Dejarlo pasar permitiría eludir comprobaciones por nombre.
        Assert.Equal(expected, SafeRelativePath.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("...")]
    public void Normalize_RejectsPathsWithoutAnyName(string input)
    {
        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(input));
    }

    [Fact]
    public void Normalize_RejectsExcessiveNesting()
    {
        var deep = string.Join('/', Enumerable.Repeat("a", 50)) + "/fichero.txt";

        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(deep));
    }

    [Fact]
    public void Normalize_RejectsOverlyLongSegments()
    {
        var longName = new string('a', 300) + ".txt";

        Assert.Throws<UnsafePathException>(() => SafeRelativePath.Normalize(longName));
    }

    [Fact]
    public void TryNormalize_ReportsFailureWithoutThrowing()
    {
        Assert.False(SafeRelativePath.TryNormalize("../escape.txt", out var failed));
        Assert.Equal(string.Empty, failed);

        Assert.True(SafeRelativePath.TryNormalize("./ok.txt", out var succeeded));
        Assert.Equal("ok.txt", succeeded);
    }

    [Fact]
    public void CombineWithin_KeepsResultInsideTheBaseDirectory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "airdrop-test");

        var result = SafeRelativePath.CombineWithin(baseDirectory, "./fotos/IMG_0001.HEIC");

        Assert.StartsWith(Path.GetFullPath(baseDirectory), result);
        Assert.EndsWith("IMG_0001.HEIC", result);
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("../../fuera.txt")]
    [InlineData("C:\\Windows\\algo.dll")]
    public void CombineWithin_RejectsAnythingEscapingTheBase(string relativePath)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "airdrop-test");

        Assert.Throws<UnsafePathException>(
            () => SafeRelativePath.CombineWithin(baseDirectory, relativePath));
    }

    [Fact]
    public void CombineWithin_DoesNotConfuseSiblingDirectoriesWithASharedPrefix()
    {
        // "C:\descargas-otro" empieza por "C:\descargas" como cadena, pero no está dentro.
        // Comparar sin el separador final dejaría pasar exactamente este caso.
        var baseDirectory = Path.Combine(Path.GetTempPath(), "descargas");

        var result = SafeRelativePath.CombineWithin(baseDirectory, "fichero.txt");

        var parent = Path.GetDirectoryName(Path.GetFullPath(baseDirectory))!;
        Assert.DoesNotContain(Path.Combine(parent, "descargas-otro"), result);
        Assert.StartsWith(
            Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar,
            result);
    }
}
