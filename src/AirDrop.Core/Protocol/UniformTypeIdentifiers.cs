namespace AirDrop.Core.Protocol;

/// <summary>
/// Uniform Type Identifiers de Apple, usados en la clave <c>FileType</c> de <c>/Ask</c>.
/// </summary>
/// <remarks>
/// El UTI le dice al iPhone qué hacer con el fichero: una imagen con
/// <see cref="Jpeg"/> va a Fotos, mientras que la misma imagen declarada como <see cref="Data"/>
/// acaba en Archivos. Acertar con el UTI es lo que hace que la transferencia se sienta nativa.
/// </remarks>
public static class UniformTypeIdentifiers
{
    /// <summary>Tipo genérico. Es el fallback: el receptor lo trata como un fichero opaco.</summary>
    public const string Data = "public.data";

    public const string Folder = "public.folder";
    public const string Jpeg = "public.jpeg";
    public const string Png = "public.png";
    public const string Heic = "public.heic";
    public const string Gif = "com.compuserve.gif";
    public const string Pdf = "com.adobe.pdf";
    public const string Mpeg4 = "public.mpeg-4";
    public const string QuickTimeMovie = "com.apple.quicktime-movie";
    public const string ZipArchive = "public.zip-archive";
    public const string PlainText = "public.plain-text";

    private static readonly Dictionary<string, string> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Imágenes
            [".jpg"] = Jpeg,
            [".jpeg"] = Jpeg,
            [".png"] = Png,
            [".heic"] = Heic,
            [".heif"] = "public.heif",
            [".gif"] = Gif,
            [".bmp"] = "com.microsoft.bmp",
            [".tif"] = "public.tiff",
            [".tiff"] = "public.tiff",
            [".webp"] = "org.webmproject.webp",
            [".svg"] = "public.svg-image",

            // Vídeo
            [".mp4"] = Mpeg4,
            [".m4v"] = "com.apple.m4v-video",
            [".mov"] = QuickTimeMovie,
            [".avi"] = "public.avi",
            [".mkv"] = "org.matroska.mkv",

            // Audio
            [".mp3"] = "public.mp3",
            [".m4a"] = "com.apple.m4a-audio",
            [".wav"] = "com.microsoft.waveform-audio",
            [".aac"] = "public.aac-audio",
            [".flac"] = "org.xiph.flac",

            // Documentos
            [".pdf"] = Pdf,
            [".txt"] = PlainText,
            [".rtf"] = "public.rtf",
            [".html"] = "public.html",
            [".htm"] = "public.html",
            [".csv"] = "public.comma-separated-values-text",
            [".json"] = "public.json",
            [".xml"] = "public.xml",
            [".doc"] = "com.microsoft.word.doc",
            [".docx"] = "org.openxmlformats.wordprocessingml.document",
            [".xls"] = "com.microsoft.excel.xls",
            [".xlsx"] = "org.openxmlformats.spreadsheetml.sheet",
            [".ppt"] = "com.microsoft.powerpoint.ppt",
            [".pptx"] = "org.openxmlformats.presentationml.presentation",

            // Archivos comprimidos
            [".zip"] = ZipArchive,
            [".gz"] = "org.gnu.gnu-zip-archive",
            [".tar"] = "public.tar-archive",
            [".7z"] = "org.7-zip.7-zip-archive",

            // Otros
            [".vcf"] = "public.vcard",
            [".ics"] = "com.apple.ical.ics",
        };

    /// <summary>
    /// Deduce el UTI a partir del nombre o la ruta de un fichero.
    /// </summary>
    /// <returns>
    /// El UTI correspondiente, o <see cref="Data"/> si la extensión no se reconoce. Nunca falla:
    /// una extensión desconocida no debe impedir el envío.
    /// </returns>
    public static string ForFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension)
            ? Data
            : ByExtension.GetValueOrDefault(extension, Data);
    }

    /// <summary>Indica si el UTI corresponde a un contenido que iOS guarda en Fotos.</summary>
    public static bool IsVisualMedia(string uniformTypeIdentifier) =>
        uniformTypeIdentifier is Jpeg or Png or Heic or "public.heif" or Gif or "public.tiff"
            or Mpeg4 or QuickTimeMovie or "com.apple.m4v-video";
}
