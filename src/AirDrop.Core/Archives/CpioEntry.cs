namespace AirDrop.Core.Archives;

/// <summary>Una entrada dentro de un archivo CPIO.</summary>
/// <param name="Name">Ruta relativa dentro del archivo, p. ej. <c>./IMG_0001.HEIC</c>.</param>
/// <param name="Mode">Modo POSIX: tipo de fichero en los bits altos y permisos en los bajos.</param>
/// <param name="DataLength">Tamaño en bytes del contenido que sigue a la cabecera.</param>
/// <param name="ModifiedTime">Fecha de modificación.</param>
public sealed record CpioEntry(
    string Name,
    int Mode,
    long DataLength,
    DateTimeOffset ModifiedTime)
{
    /// <summary>Máscara del tipo de fichero dentro del modo POSIX (<c>S_IFMT</c>).</summary>
    public const int FileTypeMask = 0xF000;

    /// <summary>Directorio (<c>S_IFDIR</c>).</summary>
    public const int Directory = 0x4000;

    /// <summary>Fichero regular (<c>S_IFREG</c>).</summary>
    public const int RegularFile = 0x8000;

    /// <summary>Enlace simbólico (<c>S_IFLNK</c>).</summary>
    public const int SymbolicLink = 0xA000;

    /// <summary>Nombre de la entrada terminadora obligatoria de todo archivo CPIO.</summary>
    public const string TrailerName = "TRAILER!!!";

    public bool IsDirectory => (Mode & FileTypeMask) == Directory;

    public bool IsRegularFile => (Mode & FileTypeMask) == RegularFile;

    /// <summary>
    /// Los enlaces simbólicos nunca se materializan al extraer: su "contenido" es una ruta que
    /// podría apuntar fuera del directorio de destino.
    /// </summary>
    public bool IsSymbolicLink => (Mode & FileTypeMask) == SymbolicLink;
}

/// <summary>Variantes del formato CPIO que aparecen en AirDrop.</summary>
public enum CpioFormat
{
    /// <summary>POSIX portable ASCII, cabeceras en octal. Magic <c>070707</c>.</summary>
    Odc,

    /// <summary>SVR4 "new ASCII", cabeceras en hexadecimal y alineación a 4 bytes. Magic <c>070701</c>.</summary>
    Newc,

    /// <summary>SVR4 con CRC. Magic <c>070702</c>. Se lee igual que <see cref="Newc"/>.</summary>
    NewcCrc,
}

/// <summary>Error de formato al leer o escribir un archivo CPIO.</summary>
public sealed class CpioFormatException(string message) : Exception(message);
