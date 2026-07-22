using AirDrop.Core.Protocol.Ble;
using Xunit;

namespace AirDrop.Core.Tests.Protocol.Ble;

/// <summary>
/// Tests del mensaje BLE de Continuity que usa AirDrop.
/// </summary>
/// <remarks>
/// Los vectores construidos a mano siguen el formato documentado en docs/01 §3.1, para verificar
/// que producimos exactamente lo que emite un dispositivo Apple y no solo algo que nuestro propio
/// parser sepa leer.
/// </remarks>
public class AppleContinuityTests
{
    [Fact]
    public void AirDropAdvertisement_HasTheExactLayoutAppleUses()
    {
        var advertisement = new AirDropAdvertisement(
            Version: 0x01,
            AppleIdHash: [0xAA, 0xBB],
            PhoneHash: [0xCC, 0xDD],
            EmailHash: [0xEE, 0xFF],
            Email2Hash: [0x11, 0x22]);

        var bytes = advertisement.ToBytes();

        Assert.Equal(AirDropAdvertisement.TotalLength, bytes.Length);
        Assert.Equal(0x05, bytes[0]);   // tipo AirDrop
        Assert.Equal(0x12, bytes[1]);   // longitud declarada: 18

        // Los bytes 2 a 9 son ceros reservados.
        Assert.All(bytes[2..10], b => Assert.Equal(0, b));

        Assert.Equal(0x01, bytes[10]);  // versión
        Assert.Equal<byte[]>([0xAA, 0xBB], bytes[11..13]);
        Assert.Equal<byte[]>([0xCC, 0xDD], bytes[13..15]);
        Assert.Equal<byte[]>([0xEE, 0xFF], bytes[15..17]);
        Assert.Equal<byte[]>([0x11, 0x22], bytes[17..19]);
        Assert.Equal(0x00, bytes[19]);  // terminador
    }

    [Fact]
    public void Anonymous_CarriesNoIdentity()
    {
        // En modo "Todos" los hashes no aportan nada y emitirlos expondría los identificadores
        // del usuario a cualquiera que escuche; están documentados como reversibles.
        var advertisement = AirDropAdvertisement.Anonymous();

        Assert.True(advertisement.IsAnonymous);
        Assert.All(advertisement.ToBytes()[11..19], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TryParse_ReadsAnAdvertisementBuiltByHand()
    {
        // Carga tal y como llega en los datos de fabricante, sin el identificador de compañía.
        byte[] manufacturerData =
        [
            0x05, 0x12,                                  // tipo AirDrop, longitud 18
            0, 0, 0, 0, 0, 0, 0, 0,                      // reservado
            0x01,                                        // versión
            0xAA, 0xBB,                                  // hash del Apple ID
            0xCC, 0xDD,                                  // hash del teléfono
            0xEE, 0xFF,                                  // hash del email
            0x11, 0x22,                                  // hash del email 2
            0x00,
        ];

        Assert.True(AirDropAdvertisement.TryParse(manufacturerData, out var result));
        Assert.NotNull(result);
        Assert.Equal(0x01, result.Version);
        Assert.Equal<byte[]>([0xAA, 0xBB], result.AppleIdHash);
        Assert.Equal<byte[]>([0x11, 0x22], result.Email2Hash);
        Assert.False(result.IsAnonymous);
    }

    [Fact]
    public void TryParse_FindsAirDropAmongOtherContinuityMessages()
    {
        // Un anuncio real encadena varios mensajes TLV, y AirDrop no tiene por qué ser el primero.
        byte[] manufacturerData =
        [
            0x10, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05,    // NearbyInfo por delante
            0x05, 0x12,                                  // AirDrop
            0, 0, 0, 0, 0, 0, 0, 0,
            0x01,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x00,
        ];

        Assert.True(AirDropAdvertisement.TryParse(manufacturerData, out var result));
        Assert.NotNull(result);
        Assert.Equal<byte[]>([0x01, 0x02], result.AppleIdHash);
    }

    [Fact]
    public void TryParse_IgnoresAdvertisementsWithoutAirDrop()
    {
        // Un iPhone emite mensajes de Continuity constantemente sin estar haciendo AirDrop.
        byte[] nearbyInfoOnly = [0x10, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05];

        Assert.False(AirDropAdvertisement.TryParse(nearbyInfoOnly, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_RejectsTruncatedMessages()
    {
        // Llega por radio desde cualquier dispositivo cercano: no puede hacernos leer de más.
        byte[] truncated = [0x05, 0x12, 0x00, 0x00];

        Assert.False(AirDropAdvertisement.TryParse(truncated, out _));
    }

    [Fact]
    public void TryParse_HandlesEmptyData()
    {
        Assert.False(AirDropAdvertisement.TryParse([], out _));
    }

    [Fact]
    public void RoundTrip_PreservesTheMessage()
    {
        var original = new AirDropAdvertisement(0x01, [0x12, 0x34], [0x56, 0x78], [0x9A, 0xBC], [0xDE, 0xF0]);

        Assert.True(AirDropAdvertisement.TryParse(original.ToBytes(), out var result));
        Assert.Equal(original, result);
    }

    [Fact]
    public void ListMessageTypes_EnumeratesEveryMessageInTheAdvertisement()
    {
        byte[] manufacturerData =
        [
            0x10, 0x02, 0x01, 0x02,                      // NearbyInfo
            0x0C, 0x03, 0x01, 0x02, 0x03,                // Handoff
            0x05, 0x02, 0x00, 0x00,                      // AirDrop (corto)
        ];

        var types = AirDropAdvertisement.ListMessageTypes(manufacturerData);

        Assert.Equal(
            [ContinuityMessageType.NearbyInfo, ContinuityMessageType.Handoff, ContinuityMessageType.AirDrop],
            types);
    }

    [Fact]
    public void ManufacturerPayload_StartsWithAppleCompanyIdInLittleEndian()
    {
        var payload = ContinuityAdvertisement.BuildManufacturerPayload(
            AirDropAdvertisement.Anonymous());

        // 0x004C en little-endian, como exige el formato de anuncios Bluetooth.
        Assert.Equal(0x4C, payload[0]);
        Assert.Equal(0x00, payload[1]);
        Assert.Equal(0x05, payload[2]);   // el mensaje de AirDrop empieza justo después
        Assert.Equal(AirDropAdvertisement.TotalLength + 2, payload.Length);
    }

    [Fact]
    public void Constructor_TruncatesFullLengthHashes()
    {
        // Permite pasar un SHA-256 completo sin que quien llama tenga que acordarse de truncarlo.
        var fullHash = new byte[32];
        fullHash[0] = 0xAB;
        fullHash[1] = 0xCD;
        fullHash[2] = 0xEF;

        var bytes = new AirDropAdvertisement(0x01, fullHash, fullHash, fullHash, fullHash).ToBytes();

        Assert.Equal(0xAB, bytes[11]);
        Assert.Equal(0xCD, bytes[12]);
        // El tercer byte del hash no debe haberse colado en el campo siguiente.
        Assert.Equal(0xAB, bytes[13]);
    }
}
