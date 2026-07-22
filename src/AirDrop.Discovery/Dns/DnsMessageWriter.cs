using System.Buffers.Binary;
using System.Text;

namespace AirDrop.Discovery.Dns;

/// <summary>
/// Codificador de mensajes DNS, con compresión de nombres.
/// </summary>
/// <remarks>
/// La compresión importa de verdad aquí: una respuesta de AirDrop repite el nombre de instancia en
/// los registros PTR, SRV y TXT, y el nombre del host en el SRV y en los A/AAAA. Sin comprimir, la
/// respuesta puede pasarse del MTU y fragmentarse, lo que en multicast es una fuente clásica de
/// descubrimientos que fallan de forma intermitente.
/// </remarks>
public static class DnsMessageWriter
{
    private const ushort ClassInternet = 1;
    private const ushort ClassHighBit = 0x8000;
    private const byte CompressionPointerMask = 0xC0;

    /// <summary>Offset máximo direccionable por un puntero de compresión: 14 bits.</summary>
    private const int MaxCompressionOffset = 0x3FFF;

    public static byte[] Write(DnsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var buffer = new MemoryStream();
        var nameOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        WriteHeader(buffer, message);

        foreach (var question in message.Questions)
        {
            WriteName(buffer, question.Name, nameOffsets);
            WriteUInt16(buffer, (ushort)question.Type);
            WriteUInt16(buffer, (ushort)(ClassInternet |
                (question.WantsUnicastResponse ? ClassHighBit : 0)));
        }

        foreach (var record in message.Answers.Concat(message.Authorities).Concat(message.Additionals))
        {
            WriteRecord(buffer, record, nameOffsets);
        }

        return buffer.ToArray();
    }

    private static void WriteHeader(MemoryStream buffer, DnsMessage message)
    {
        var header = message.Header;
        WriteUInt16(buffer, header.Id);
        WriteUInt16(buffer, header.Flags);

        // Los contadores se derivan de las listas reales, no de los del header: así un mensaje
        // construido a mano no puede salir con contadores incoherentes.
        WriteUInt16(buffer, (ushort)message.Questions.Count);
        WriteUInt16(buffer, (ushort)message.Answers.Count);
        WriteUInt16(buffer, (ushort)message.Authorities.Count);
        WriteUInt16(buffer, (ushort)message.Additionals.Count);
    }

    private static void WriteRecord(
        MemoryStream buffer,
        DnsResourceRecord record,
        Dictionary<string, int> nameOffsets)
    {
        WriteName(buffer, record.Name, nameOffsets);
        WriteUInt16(buffer, (ushort)record.Type);
        WriteUInt16(buffer, (ushort)(ClassInternet | (record.CacheFlush ? ClassHighBit : 0)));
        WriteUInt32(buffer, (uint)Math.Max(0, record.Ttl.TotalSeconds));

        // La longitud de RDATA no se conoce hasta haberlo escrito: se reserva el hueco y se
        // rellena después. Es más simple y menos frágil que calcularla por adelantado, sobre todo
        // con nombres comprimidos de por medio.
        var lengthPosition = buffer.Position;
        WriteUInt16(buffer, 0);
        var dataStart = buffer.Position;

        WriteRecordData(buffer, record, nameOffsets);

        var dataEnd = buffer.Position;
        buffer.Position = lengthPosition;
        WriteUInt16(buffer, (ushort)(dataEnd - dataStart));
        buffer.Position = dataEnd;
    }

    private static void WriteRecordData(
        MemoryStream buffer,
        DnsResourceRecord record,
        Dictionary<string, int> nameOffsets)
    {
        switch (record)
        {
            case PtrRecord ptr:
                WriteName(buffer, ptr.Target, nameOffsets);
                break;

            case SrvRecord srv:
                WriteUInt16(buffer, srv.Priority);
                WriteUInt16(buffer, srv.Weight);
                WriteUInt16(buffer, srv.Port);
                // El destino de un SRV no se comprime en la práctica de mDNS, pero comprimirlo es
                // válido y ahorra espacio: el mismo host aparece en varios registros.
                WriteName(buffer, srv.Target, nameOffsets);
                break;

            case TxtRecord txt:
                WriteTxtStrings(buffer, txt.Strings);
                break;

            case AddressRecord address:
                buffer.Write(address.Address.GetAddressBytes());
                break;

            case UnknownRecord unknown:
                buffer.Write(unknown.Data);
                break;

            default:
                throw new DnsFormatException(
                    $"Tipo de registro no serializable: {record.GetType().Name}.");
        }
    }

    private static void WriteTxtStrings(MemoryStream buffer, IReadOnlyList<string> strings)
    {
        // Un TXT vacío debe llevar al menos un byte nulo: RDATA de longitud cero es inválido.
        if (strings.Count == 0)
        {
            buffer.WriteByte(0);
            return;
        }

        foreach (var entry in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255)
            {
                throw new DnsFormatException(
                    $"Una cadena de un registro TXT no puede pasar de 255 bytes: {bytes.Length}.");
            }

            buffer.WriteByte((byte)bytes.Length);
            buffer.Write(bytes);
        }
    }

    /// <summary>
    /// Escribe un nombre DNS, reutilizando un puntero de compresión si ya se emitió antes.
    /// </summary>
    /// <remarks>
    /// Se registra el offset de cada sufijo, no solo el del nombre completo: eso permite que
    /// <c>MiPC._airdrop._tcp.local</c> comprima contra un <c>_airdrop._tcp.local</c> ya escrito.
    /// </remarks>
    private static void WriteName(
        MemoryStream buffer,
        string name,
        Dictionary<string, int> nameOffsets)
    {
        if (string.IsNullOrEmpty(name))
        {
            buffer.WriteByte(0);
            return;
        }

        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels[i..]);

            if (nameOffsets.TryGetValue(suffix, out var offset))
            {
                WriteUInt16(buffer, (ushort)(offset | (CompressionPointerMask << 8)));
                return;
            }

            // Solo se pueden referenciar offsets que quepan en 14 bits.
            if (buffer.Position <= MaxCompressionOffset)
            {
                nameOffsets[suffix] = (int)buffer.Position;
            }

            var bytes = Encoding.UTF8.GetBytes(labels[i]);
            if (bytes.Length > 63)
            {
                throw new DnsFormatException(
                    $"Una etiqueta DNS no puede pasar de 63 bytes: '{labels[i]}' ocupa {bytes.Length}.");
            }

            buffer.WriteByte((byte)bytes.Length);
            buffer.Write(bytes);
        }

        buffer.WriteByte(0);
    }

    private static void WriteUInt16(MemoryStream buffer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        buffer.Write(bytes);
    }

    private static void WriteUInt32(MemoryStream buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        buffer.Write(bytes);
    }
}
