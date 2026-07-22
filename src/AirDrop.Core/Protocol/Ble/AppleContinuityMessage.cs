using System.Buffers.Binary;

namespace AirDrop.Core.Protocol.Ble;

/// <summary>Tipos de mensaje del protocolo Continuity de Apple observados por BLE.</summary>
public enum ContinuityMessageType : byte
{
    AirPrint = 0x03,
    AirDrop = 0x05,
    HomeKit = 0x06,
    ProximityPairing = 0x07,
    HeySiri = 0x08,
    AirPlayTarget = 0x09,
    AirPlaySource = 0x0A,
    MagicSwitch = 0x0B,
    Handoff = 0x0C,
    TetheringSource = 0x0D,
    TetheringTarget = 0x0E,
    NearbyAction = 0x0F,
    NearbyInfo = 0x10,

    /// <summary>
    /// Red Buscar (Find My). Es el anuncio más frecuente en un entorno con dispositivos Apple:
    /// lo emiten de forma continua aunque no estén haciendo nada.
    /// </summary>
    FindMy = 0x12,
}

/// <summary>
/// Mensaje de AirDrop dentro de un anuncio BLE de Continuity.
/// </summary>
/// <remarks>
/// <para>
/// Esta es la capa que <b>despierta</b> AirDrop. Cuando alguien abre la hoja de compartir en un
/// iPhone, el teléfono empieza a emitir este anuncio; los dispositivos cercanos que lo reciben
/// activan su interfaz AWDL y su servidor AirDrop. Sin este aviso, un dispositivo en reposo ni
/// siquiera tiene AWDL encendido.
/// </para>
/// <para>
/// <b>Bluetooth no transporta datos en AirDrop.</b> Este anuncio son 18 bytes de carga que solo
/// dicen "voy a hacer AirDrop" más unos hashes truncados de identidad. El descubrimiento real
/// (mDNS) y la transferencia (TLS sobre HTTP) viajan por AWDL. Ver docs/01 §2 y §3.
/// </para>
/// <para>Estructura del mensaje (docs/01 §3.1):</para>
/// <code>
///   [0]      tipo = 0x05
///   [1]      longitud = 0x12 (18)
///   [2..10)  ceros
///   [10]     versión
///   [11..13) SHA-256 truncado del Apple ID
///   [13..15) SHA-256 truncado del teléfono
///   [15..17) SHA-256 truncado del email
///   [17..19) SHA-256 truncado del email 2
///   [19]     terminador 0x00
/// </code>
/// </remarks>
public sealed record AirDropAdvertisement(
    byte Version,
    byte[] AppleIdHash,
    byte[] PhoneHash,
    byte[] EmailHash,
    byte[] Email2Hash)
{
    /// <summary>Identificador de fabricante de Apple en los anuncios BLE.</summary>
    public const ushort AppleCompanyId = 0x004C;

    /// <summary>Longitud declarada en la cabecera del mensaje.</summary>
    private const byte PayloadLength = 0x12;

    /// <summary>Longitud total del mensaje incluyendo tipo, longitud y terminador.</summary>
    public const int TotalLength = 20;

    /// <summary>Bytes de cada hash truncado.</summary>
    private const int HashLength = 2;

    /// <summary>Versión que emiten los dispositivos actuales.</summary>
    public const byte CurrentVersion = 0x01;

    /// <summary>
    /// Construye un anuncio sin identidad, con los hashes a cero.
    /// </summary>
    /// <remarks>
    /// Es lo apropiado en modo "Todos": los hashes solo sirven para el filtrado de contactos, y
    /// emitir los del usuario los expondría a cualquiera que escuche. Están documentados como
    /// reversibles por fuerza bruta.
    /// </remarks>
    public static AirDropAdvertisement Anonymous() =>
        new(CurrentVersion, new byte[HashLength], new byte[HashLength],
            new byte[HashLength], new byte[HashLength]);

    /// <summary>Serializa el mensaje tal y como viaja dentro del anuncio BLE.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[TotalLength];
        bytes[0] = (byte)ContinuityMessageType.AirDrop;
        bytes[1] = PayloadLength;
        // Los bytes 2..10 quedan a cero.
        bytes[10] = Version;

        CopyHash(AppleIdHash, bytes, 11);
        CopyHash(PhoneHash, bytes, 13);
        CopyHash(EmailHash, bytes, 15);
        CopyHash(Email2Hash, bytes, 17);

        bytes[19] = 0x00;
        return bytes;
    }

    /// <summary>
    /// Intenta interpretar un mensaje de AirDrop dentro de los datos de fabricante de un anuncio.
    /// </summary>
    /// <param name="manufacturerData">
    /// Carga del fabricante Apple, sin el identificador de compañía.
    /// </param>
    public static bool TryParse(ReadOnlySpan<byte> manufacturerData, out AirDropAdvertisement? result)
    {
        result = null;

        // Un anuncio de Continuity puede encadenar varios mensajes TLV: se recorren hasta dar con
        // el de AirDrop, en vez de asumir que es el primero.
        var position = 0;
        while (position + 2 <= manufacturerData.Length)
        {
            var type = manufacturerData[position];
            int length = manufacturerData[position + 1];
            var payloadStart = position + 2;

            if (payloadStart + length > manufacturerData.Length)
            {
                return false;   // mensaje truncado
            }

            if (type == (byte)ContinuityMessageType.AirDrop && length >= PayloadLength)
            {
                var payload = manufacturerData.Slice(payloadStart, length);

                result = new AirDropAdvertisement(
                    payload[8],
                    payload.Slice(9, HashLength).ToArray(),
                    payload.Slice(11, HashLength).ToArray(),
                    payload.Slice(13, HashLength).ToArray(),
                    payload.Slice(15, HashLength).ToArray());

                return true;
            }

            position = payloadStart + length;
        }

        return false;
    }

    /// <summary>Lista los tipos de mensaje Continuity presentes en un anuncio.</summary>
    /// <remarks>Útil para diagnosticar qué está haciendo un dispositivo Apple cercano.</remarks>
    public static IReadOnlyList<ContinuityMessageType> ListMessageTypes(
        ReadOnlySpan<byte> manufacturerData)
    {
        var types = new List<ContinuityMessageType>();
        var position = 0;

        while (position + 2 <= manufacturerData.Length)
        {
            var type = manufacturerData[position];
            int length = manufacturerData[position + 1];

            if (position + 2 + length > manufacturerData.Length)
            {
                break;
            }

            types.Add((ContinuityMessageType)type);
            position += 2 + length;
        }

        return types;
    }

    /// <summary>Indica si todos los hashes están a cero, es decir, si no declara identidad.</summary>
    public bool IsAnonymous =>
        AppleIdHash.All(b => b == 0)
        && PhoneHash.All(b => b == 0)
        && EmailHash.All(b => b == 0)
        && Email2Hash.All(b => b == 0);

    /// <summary>
    /// Compara por contenido de los hashes, no por referencia.
    /// </summary>
    /// <remarks>
    /// La igualdad que genera <c>record</c> compara los arrays por referencia, de modo que dos
    /// anuncios idénticos leídos de dos paquetes distintos saldrían diferentes. Eso rompería
    /// cualquier intento de deduplicar detecciones del mismo dispositivo.
    /// </remarks>
    public bool Equals(AirDropAdvertisement? other) =>
        other is not null
        && Version == other.Version
        && AppleIdHash.AsSpan().SequenceEqual(other.AppleIdHash)
        && PhoneHash.AsSpan().SequenceEqual(other.PhoneHash)
        && EmailHash.AsSpan().SequenceEqual(other.EmailHash)
        && Email2Hash.AsSpan().SequenceEqual(other.Email2Hash);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.AddBytes(AppleIdHash);
        hash.AddBytes(PhoneHash);
        hash.AddBytes(EmailHash);
        hash.AddBytes(Email2Hash);
        return hash.ToHashCode();
    }

    private static void CopyHash(byte[] source, byte[] destination, int offset)
    {
        // Se toman solo los bytes que caben: los hashes llegan truncados a 2 bytes, pero aceptar
        // un SHA-256 completo y truncarlo aquí evita que quien llame tenga que acordarse.
        var length = Math.Min(HashLength, source.Length);
        source.AsSpan(0, length).CopyTo(destination.AsSpan(offset, length));
    }
}

/// <summary>Utilidades para componer y leer la carga de fabricante de un anuncio de Continuity.</summary>
public static class ContinuityAdvertisement
{
    /// <summary>
    /// Compone la carga completa del fabricante, con el identificador de Apple al principio.
    /// </summary>
    /// <remarks>
    /// El identificador va en little-endian, como exige el formato de anuncios de Bluetooth.
    /// </remarks>
    public static byte[] BuildManufacturerPayload(AirDropAdvertisement advertisement)
    {
        var message = advertisement.ToBytes();
        var payload = new byte[2 + message.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(payload, AirDropAdvertisement.AppleCompanyId);
        message.CopyTo(payload, 2);

        return payload;
    }
}
