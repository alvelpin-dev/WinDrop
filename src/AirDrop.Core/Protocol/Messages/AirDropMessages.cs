using AirDrop.Core.Protocol.Plist;

namespace AirDrop.Core.Protocol.Messages;

/// <summary>
/// Nombres de las claves del protocolo AirDrop.
/// </summary>
/// <remarks>
/// Centralizadas en un único sitio: son cadenas literales que deben coincidir exactamente con lo
/// que espera un dispositivo Apple, y una errata aquí produce un fallo silencioso de
/// interoperabilidad muy difícil de diagnosticar.
/// </remarks>
public static class AirDropKeys
{
    public const string SenderComputerName = "SenderComputerName";
    public const string SenderModelName = "SenderModelName";
    public const string SenderRecordData = "SenderRecordData";
    public const string SenderId = "SenderID";
    public const string BundleId = "BundleID";
    public const string ConvertMediaFormats = "ConvertMediaFormats";
    public const string FileIcon = "FileIcon";
    public const string Files = "Files";
    public const string FileName = "FileName";
    public const string FileType = "FileType";
    public const string FileBomPath = "FileBomPath";
    public const string FileIsDirectory = "FileIsDirectory";
    public const string ReceiverComputerName = "ReceiverComputerName";
    public const string ReceiverModelName = "ReceiverModelName";
    public const string ReceiverMediaCapabilities = "ReceiverMediaCapabilities";
    public const string Version = "Version";
}

/// <summary>
/// Petición <c>POST /Discover</c>: el emisor se presenta y pregunta si somos un destino válido.
/// </summary>
/// <param name="SenderRecordData">
/// Blob PKCS#7 firmado por la CA de Apple con los hashes de contacto del emisor. Es opcional:
/// un emisor en modo "Todos" puede omitirlo, y nosotros nunca lo generamos porque requeriría un
/// certificado de Apple ID firmado por Apple (ver docs/01 §6.5).
/// </param>
public sealed record DiscoverRequest(byte[]? SenderRecordData)
{
    public static DiscoverRequest FromPlist(PlistDictionary plist)
    {
        ArgumentNullException.ThrowIfNull(plist);
        return new DiscoverRequest(plist.GetData(AirDropKeys.SenderRecordData));
    }

    public PlistDictionary ToPlist() =>
        new PlistDictionaryBuilder()
            .SetIfNotNull(
                AirDropKeys.SenderRecordData,
                SenderRecordData is null ? null : new PlistData(SenderRecordData))
            .Build();
}

/// <summary>
/// Respuesta a <c>/Discover</c>: nuestra identidad, que es lo que el emisor muestra en su UI.
/// </summary>
/// <param name="ReceiverComputerName">Nombre visible en la hoja de AirDrop del emisor.</param>
/// <param name="ReceiverModelName">Identificador de modelo, p. ej. <c>Windows11,1</c>.</param>
/// <param name="ReceiverMediaCapabilities">
/// JSON en UTF-8 embebido como datos binarios dentro del plist. Declara qué formatos de medios
/// aceptamos sin conversión.
/// </param>
public sealed record DiscoverResponse(
    string ReceiverComputerName,
    string ReceiverModelName,
    byte[]? ReceiverMediaCapabilities = null)
{
    public static DiscoverResponse FromPlist(PlistDictionary plist)
    {
        ArgumentNullException.ThrowIfNull(plist);

        return new DiscoverResponse(
            plist.GetString(AirDropKeys.ReceiverComputerName)
                ?? throw new PlistFormatException($"Falta {AirDropKeys.ReceiverComputerName}."),
            plist.GetString(AirDropKeys.ReceiverModelName) ?? string.Empty,
            plist.GetData(AirDropKeys.ReceiverMediaCapabilities));
    }

    public PlistDictionary ToPlist() =>
        new PlistDictionaryBuilder()
            .Set(AirDropKeys.ReceiverComputerName, ReceiverComputerName)
            .Set(AirDropKeys.ReceiverModelName, ReceiverModelName)
            .SetIfNotNull(
                AirDropKeys.ReceiverMediaCapabilities,
                ReceiverMediaCapabilities is null ? null : new PlistData(ReceiverMediaCapabilities))
            .Build();
}

/// <summary>Metadatos de un fichero incluidos en <c>/Ask</c>.</summary>
/// <param name="FileName">Nombre visible del fichero.</param>
/// <param name="FileType">Uniform Type Identifier, p. ej. <c>public.jpeg</c>.</param>
/// <param name="FileBomPath">Ruta dentro del archivo CPIO, típicamente <c>./NombreFichero</c>.</param>
/// <param name="IsDirectory">Indica si la entrada es una carpeta.</param>
public sealed record AirDropFileMetadata(
    string FileName,
    string FileType,
    string FileBomPath,
    bool IsDirectory = false)
{
    /// <summary>Construye los metadatos de un fichero suelto, con la ruta BOM convencional.</summary>
    public static AirDropFileMetadata ForFile(string fileName, string uniformTypeIdentifier) =>
        new(fileName, uniformTypeIdentifier, $"./{fileName}");

    public static AirDropFileMetadata FromPlist(PlistDictionary plist)
    {
        ArgumentNullException.ThrowIfNull(plist);

        var name = plist.GetString(AirDropKeys.FileName)
            ?? throw new PlistFormatException($"Falta {AirDropKeys.FileName} en un elemento de Files.");

        return new AirDropFileMetadata(
            name,
            plist.GetString(AirDropKeys.FileType) ?? UniformTypeIdentifiers.Data,
            plist.GetString(AirDropKeys.FileBomPath) ?? $"./{name}",
            plist.GetBoolean(AirDropKeys.FileIsDirectory) ?? false);
    }

    public PlistDictionary ToPlist() =>
        new PlistDictionaryBuilder()
            .Set(AirDropKeys.FileName, FileName)
            .Set(AirDropKeys.FileType, FileType)
            .Set(AirDropKeys.FileBomPath, FileBomPath)
            .Set(AirDropKeys.FileIsDirectory, IsDirectory)
            .Build();
}

/// <summary>
/// Petición <c>POST /Ask</c>: el emisor pide permiso para enviar y describe lo que va a mandar.
/// </summary>
/// <remarks>
/// El receptor mantiene esta petición abierta mientras el usuario decide. Cualquier servidor que
/// la atienda debe tolerar respuestas que tarden minutos.
/// </remarks>
public sealed record AskRequest(
    string SenderComputerName,
    string SenderModelName,
    IReadOnlyList<AirDropFileMetadata> Files,
    string? SenderId = null,
    string? BundleId = null,
    bool ConvertMediaFormats = false,
    byte[]? FileIcon = null)
{
    public static AskRequest FromPlist(PlistDictionary plist)
    {
        ArgumentNullException.ThrowIfNull(plist);

        var files = new List<AirDropFileMetadata>();
        if (plist.GetArray(AirDropKeys.Files) is { } array)
        {
            foreach (var item in array.Items)
            {
                if (item is PlistDictionary fileDictionary)
                {
                    files.Add(AirDropFileMetadata.FromPlist(fileDictionary));
                }
            }
        }

        return new AskRequest(
            plist.GetString(AirDropKeys.SenderComputerName)
                ?? throw new PlistFormatException($"Falta {AirDropKeys.SenderComputerName}."),
            plist.GetString(AirDropKeys.SenderModelName) ?? string.Empty,
            files,
            plist.GetString(AirDropKeys.SenderId),
            plist.GetString(AirDropKeys.BundleId),
            plist.GetBoolean(AirDropKeys.ConvertMediaFormats) ?? false,
            plist.GetData(AirDropKeys.FileIcon));
    }

    public PlistDictionary ToPlist()
    {
        var files = new PlistArray([.. Files.Select(PlistValue (f) => f.ToPlist())]);

        return new PlistDictionaryBuilder()
            .Set(AirDropKeys.SenderComputerName, SenderComputerName)
            .Set(AirDropKeys.SenderModelName, SenderModelName)
            .Set(AirDropKeys.Files, files)
            .Set(AirDropKeys.ConvertMediaFormats, ConvertMediaFormats)
            .SetIfNotNull(AirDropKeys.SenderId, SenderId is null ? null : new PlistString(SenderId))
            .SetIfNotNull(AirDropKeys.BundleId, BundleId is null ? null : new PlistString(BundleId))
            // FileIcon va en JPEG 2000, que .NET no sabe generar. Se omite al enviar.
            // Pendiente del test 4 del plan de validación: confirmar que el iPhone lo tolera.
            .SetIfNotNull(AirDropKeys.FileIcon, FileIcon is null ? null : new PlistData(FileIcon))
            .Build();
    }
}

/// <summary>Respuesta afirmativa a <c>/Ask</c>. El rechazo se expresa con un código HTTP de error.</summary>
public sealed record AskResponse(string ReceiverComputerName, string ReceiverModelName)
{
    public static AskResponse FromPlist(PlistDictionary plist)
    {
        ArgumentNullException.ThrowIfNull(plist);

        return new AskResponse(
            plist.GetString(AirDropKeys.ReceiverComputerName) ?? string.Empty,
            plist.GetString(AirDropKeys.ReceiverModelName) ?? string.Empty);
    }

    public PlistDictionary ToPlist() =>
        new PlistDictionaryBuilder()
            .Set(AirDropKeys.ReceiverComputerName, ReceiverComputerName)
            .Set(AirDropKeys.ReceiverModelName, ReceiverModelName)
            .Build();
}
