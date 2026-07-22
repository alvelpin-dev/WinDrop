using AirDrop.Core.Protocol;
using AirDrop.Core.Protocol.Messages;
using AirDrop.Core.Protocol.Plist;
using Xunit;

namespace AirDrop.Core.Tests.Protocol;

public class AirDropMessagesTests
{
    [Fact]
    public void AskRequest_SurvivesFullPlistRoundTrip()
    {
        var original = new AskRequest(
            SenderComputerName: "PC de Álvaro",
            SenderModelName: "Windows11,1",
            Files:
            [
                AirDropFileMetadata.ForFile("IMG_0001.HEIC", UniformTypeIdentifiers.Heic),
                AirDropFileMetadata.ForFile("informe.pdf", UniformTypeIdentifiers.Pdf),
            ],
            SenderId: "ABCDEF0123456789",
            BundleId: "com.apple.finder",
            ConvertMediaFormats: true);

        // Round trip completo pasando por bytes, que es lo que viaja por el cable.
        var bytes = BinaryPlistWriter.Write(original.ToPlist());
        var result = AskRequest.FromPlist(BinaryPlistReader.ReadDictionary(bytes));

        Assert.Equal(original.SenderComputerName, result.SenderComputerName);
        Assert.Equal(original.SenderModelName, result.SenderModelName);
        Assert.Equal(original.SenderId, result.SenderId);
        Assert.Equal(original.BundleId, result.BundleId);
        Assert.True(result.ConvertMediaFormats);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("IMG_0001.HEIC", result.Files[0].FileName);
        Assert.Equal(UniformTypeIdentifiers.Heic, result.Files[0].FileType);
        Assert.Equal("./IMG_0001.HEIC", result.Files[0].FileBomPath);
        Assert.Equal(UniformTypeIdentifiers.Pdf, result.Files[1].FileType);
    }

    [Fact]
    public void AskRequest_OmitsOptionalKeysWhenAbsent()
    {
        // Las claves opcionales no deben aparecer con valor vacío: un receptor podría
        // interpretar "" como un valor real en vez de como ausencia.
        var minimal = new AskRequest("PC", "Windows11,1", []);

        var plist = minimal.ToPlist();

        Assert.False(plist.Entries.ContainsKey(AirDropKeys.SenderId));
        Assert.False(plist.Entries.ContainsKey(AirDropKeys.BundleId));
        Assert.False(plist.Entries.ContainsKey(AirDropKeys.FileIcon));
    }

    [Fact]
    public void AskRequest_ThrowsWhenSenderNameIsMissing()
    {
        var incomplete = new PlistDictionaryBuilder()
            .Set(AirDropKeys.SenderModelName, "iPhone17,1")
            .Build();

        Assert.Throws<PlistFormatException>(() => AskRequest.FromPlist(incomplete));
    }

    [Fact]
    public void AskRequest_IgnoresNonDictionaryEntriesInFilesArray()
    {
        // Robustez frente a un emisor que mande basura en el array: se ignora lo que no
        // encaja en vez de tirar la transferencia entera.
        var plist = new PlistDictionaryBuilder()
            .Set(AirDropKeys.SenderComputerName, "iPhone de prueba")
            .Set(AirDropKeys.Files, new PlistArray(
                new PlistString("esto no debería estar aquí"),
                new PlistDictionaryBuilder()
                    .Set(AirDropKeys.FileName, "valido.jpg")
                    .Build()))
            .Build();

        var result = AskRequest.FromPlist(plist);

        Assert.Single(result.Files);
        Assert.Equal("valido.jpg", result.Files[0].FileName);
    }

    [Fact]
    public void FileMetadata_FallsBackToSensibleDefaults()
    {
        // Solo FileName presente: el resto debe rellenarse sin fallar.
        var plist = new PlistDictionaryBuilder()
            .Set(AirDropKeys.FileName, "misterio.xyz")
            .Build();

        var result = AirDropFileMetadata.FromPlist(plist);

        Assert.Equal(UniformTypeIdentifiers.Data, result.FileType);
        Assert.Equal("./misterio.xyz", result.FileBomPath);
        Assert.False(result.IsDirectory);
    }

    [Fact]
    public void DiscoverResponse_SurvivesRoundTrip()
    {
        var original = new DiscoverResponse("PC de Álvaro", "Windows11,1", "{\"Version\":1}"u8.ToArray());

        var bytes = BinaryPlistWriter.Write(original.ToPlist());
        var result = DiscoverResponse.FromPlist(BinaryPlistReader.ReadDictionary(bytes));

        Assert.Equal(original.ReceiverComputerName, result.ReceiverComputerName);
        Assert.Equal(original.ReceiverModelName, result.ReceiverModelName);
        Assert.Equal(original.ReceiverMediaCapabilities, result.ReceiverMediaCapabilities);
    }

    [Fact]
    public void DiscoverRequest_HandlesSenderRecordDataBeingAbsent()
    {
        // Un emisor en modo "Todos" puede no enviar SenderRecordData. No es un error.
        var result = DiscoverRequest.FromPlist(new PlistDictionary());

        Assert.Null(result.SenderRecordData);
        Assert.Empty(result.ToPlist().Entries);
    }
}

public class AirDropFlagsTests
{
    [Fact]
    public void SupportedFlags_NeverAnnounceDvZip()
    {
        // DVZip es un formato propietario cerrado que no sabemos decodificar. Anunciarlo haría
        // que el emisor nos mandara datos ilegibles y rompería la transferencia.
        Assert.False(AirDropFlags.Supported.HasFlag(AirDropFlags.SupportsDvZip));
    }

    [Fact]
    public void SupportedFlags_NeverAnnounceUnattendedPush()
    {
        // Permitiría /Upload sin /Ask, es decir, recibir ficheros sin consentimiento del usuario.
        Assert.False(AirDropFlags.Supported.HasFlag(AirDropFlags.SupportsUnattendedPush));
    }

    [Fact]
    public void SupportedFlags_AnnounceWhatIsActuallyImplemented()
    {
        Assert.True(AirDropFlags.Supported.HasFlag(AirDropFlags.SupportsDiscover));
        Assert.True(AirDropFlags.Supported.HasFlag(AirDropFlags.SupportsMixedTypes));
    }
}

public class UniformTypeIdentifiersTests
{
    [Theory]
    [InlineData("foto.jpg", "public.jpeg")]
    [InlineData("FOTO.JPEG", "public.jpeg")]          // la extensión no distingue mayúsculas
    [InlineData("IMG_0001.HEIC", "public.heic")]
    [InlineData("clip.mov", "com.apple.quicktime-movie")]
    [InlineData("informe.pdf", "com.adobe.pdf")]
    [InlineData("archivo.zip", "public.zip-archive")]
    [InlineData("hoja.xlsx", "org.openxmlformats.spreadsheetml.sheet")]
    public void ForFileName_MapsKnownExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, UniformTypeIdentifiers.ForFileName(fileName));
    }

    [Theory]
    [InlineData("misterio.xyz")]
    [InlineData("sin_extension")]
    public void ForFileName_FallsBackToDataForUnknownTypes(string fileName)
    {
        // Una extensión desconocida nunca debe impedir el envío.
        Assert.Equal(UniformTypeIdentifiers.Data, UniformTypeIdentifiers.ForFileName(fileName));
    }

    [Fact]
    public void ForFileName_UsesTheLastExtension()
    {
        Assert.Equal(UniformTypeIdentifiers.Pdf, UniformTypeIdentifiers.ForFileName("copia.zip.pdf"));
    }

    [Fact]
    public void IsVisualMedia_DistinguishesPhotosFromDocuments()
    {
        // Determina si iOS lo guarda en Fotos o en Archivos.
        Assert.True(UniformTypeIdentifiers.IsVisualMedia(UniformTypeIdentifiers.Heic));
        Assert.True(UniformTypeIdentifiers.IsVisualMedia(UniformTypeIdentifiers.Mpeg4));
        Assert.False(UniformTypeIdentifiers.IsVisualMedia(UniformTypeIdentifiers.Pdf));
        Assert.False(UniformTypeIdentifiers.IsVisualMedia(UniformTypeIdentifiers.Data));
    }
}
