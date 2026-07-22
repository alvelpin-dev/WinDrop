namespace AirDrop.Core.Storage;

/// <summary>
/// Saneado de las rutas que llegan dentro de un archivo CPIO.
/// </summary>
/// <remarks>
/// <para>
/// <b>Esta clase es una frontera de seguridad.</b> Los nombres de fichero de una transferencia
/// entrante los elige íntegramente el emisor, que es un dispositivo no confiable en la red local.
/// Un archivo con una entrada llamada <c>../../../Windows/System32/algo.dll</c> permitiría escribir
/// fuera de la carpeta de descargas: escritura arbitraria de ficheros con los permisos del usuario.
/// </para>
/// <para>
/// AirDrop admite enviar carpetas, así que no basta con quedarse con el nombre base: hay que
/// permitir subdirectorios legítimos y rechazar únicamente lo que se escapa. La política es
/// <b>lista blanca</b>: se rechaza todo lo que no se entienda, en vez de intentar filtrar los
/// patrones peligrosos conocidos.
/// </para>
/// </remarks>
public static class SafeRelativePath
{
    /// <summary>Nombres reservados por Windows, que no pueden usarse ni con extensión.</summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    /// <summary>Límite por segmento, para no chocar con los límites del sistema de ficheros.</summary>
    private const int MaxSegmentLength = 255;

    /// <summary>Profundidad máxima de anidamiento aceptada.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Convierte una ruta de un archivo en una ruta relativa segura.
    /// </summary>
    /// <param name="path">Ruta tal y como viene en el archivo, p. ej. <c>./fotos/IMG_0001.HEIC</c>.</param>
    /// <returns>Ruta relativa normalizada con separadores del sistema.</returns>
    /// <exception cref="UnsafePathException">La ruta no es segura y debe rechazarse.</exception>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UnsafePathException("Ruta vacía.");
        }

        // Los separadores se unifican antes de nada: en Windows la barra invertida también separa,
        // así que un nombre "a\..\..\b" creado en un origen POSIX sería peligroso aquí.
        var unified = path.Replace('\\', '/');

        if (Path.IsPathRooted(unified) || unified.StartsWith('/'))
        {
            throw new UnsafePathException($"Ruta absoluta no permitida: '{path}'.");
        }

        // Unidad de Windows ("C:algo") o UNC.
        if (unified.Length >= 2 && unified[1] == ':')
        {
            throw new UnsafePathException($"Ruta con unidad no permitida: '{path}'.");
        }

        var segments = new List<string>();

        foreach (var rawSegment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // "." es el prefijo habitual de los CPIO de AirDrop ("./fichero"): se descarta.
            if (rawSegment == ".")
            {
                continue;
            }

            if (rawSegment == "..")
            {
                // No se resuelve subiendo un nivel: se rechaza. Resolverlo permitiría que
                // "a/../../b" escapase si el saneado se aplicara antes de conocer la base.
                throw new UnsafePathException($"La ruta intenta salir del destino: '{path}'.");
            }

            segments.Add(ValidateSegment(rawSegment, path));
        }

        if (segments.Count == 0)
        {
            throw new UnsafePathException($"La ruta no contiene ningún nombre: '{path}'.");
        }

        if (segments.Count > MaxDepth)
        {
            throw new UnsafePathException(
                $"Anidamiento excesivo ({segments.Count} niveles) en '{path}'.");
        }

        return Path.Combine([.. segments]);
    }

    /// <summary>Intenta sanear una ruta sin lanzar excepción.</summary>
    public static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = Normalize(path);
            return true;
        }
        catch (UnsafePathException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Combina un directorio base con una ruta entrante, verificando el resultado.
    /// </summary>
    /// <remarks>
    /// La comprobación final sobre la ruta ya resuelta es deliberadamente redundante con el saneado
    /// por segmentos. Es la red que atrapa cualquier caso que el análisis textual no previera:
    /// enlaces simbólicos, nombres cortos 8.3 o particularidades de normalización de Windows.
    /// </remarks>
    /// <exception cref="UnsafePathException">El resultado caería fuera de <paramref name="baseDirectory"/>.</exception>
    public static string CombineWithin(string baseDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var safeRelative = Normalize(relativePath);
        var fullBase = Path.GetFullPath(baseDirectory);
        var combined = Path.GetFullPath(Path.Combine(fullBase, safeRelative));

        var baseWithSeparator = fullBase.EndsWith(Path.DirectorySeparatorChar)
            ? fullBase
            : fullBase + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(baseWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException(
                $"La ruta resuelta '{combined}' cae fuera de '{fullBase}'.");
        }

        return combined;
    }

    private static string ValidateSegment(string segment, string originalPath)
    {
        if (segment.Length > MaxSegmentLength)
        {
            throw new UnsafePathException(
                $"Nombre de más de {MaxSegmentLength} caracteres en '{originalPath}'.");
        }

        foreach (var c in segment)
        {
            // Los caracteres de control incluyen el cero, que trunca cadenas en las APIs nativas.
            if (char.IsControl(c))
            {
                throw new UnsafePathException(
                    $"Carácter de control en el nombre de '{originalPath}'.");
            }

            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                throw new UnsafePathException(
                    $"Carácter '{c}' no permitido en un nombre de fichero de Windows.");
            }
        }

        // Windows ignora los puntos y espacios finales al resolver rutas, así que "algo." y "algo"
        // son el mismo fichero: dejarlo pasar permitiría eludir comprobaciones por nombre.
        var trimmed = segment.TrimEnd(' ', '.');
        if (trimmed.Length == 0)
        {
            throw new UnsafePathException($"Nombre vacío tras normalizar en '{originalPath}'.");
        }

        // Los nombres reservados lo son también con extensión: "CON.txt" sigue siendo la consola.
        var withoutExtension = trimmed.Split('.')[0];
        if (ReservedNames.Contains(withoutExtension))
        {
            throw new UnsafePathException(
                $"'{withoutExtension}' es un nombre reservado de Windows.");
        }

        return trimmed;
    }
}

/// <summary>Se lanza cuando una ruta entrante no es segura y debe rechazarse.</summary>
public sealed class UnsafePathException(string message) : Exception(message);
