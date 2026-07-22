using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AirDrop.Discovery.Dns;

/// <summary>
/// Decodificador de mensajes DNS, con soporte de compresión de nombres.
/// </summary>
/// <remarks>
/// Igual que el lector de plists, es defensivo por diseño: los paquetes mDNS llegan por multicast
/// desde cualquier equipo de la red local, sin autenticación de ningún tipo. Un paquete malformado
/// —o malicioso— no debe poder tumbar el proceso ni hacernos leer fuera del buffer.
/// </remarks>
public static class DnsMessageReader
{
    private const int HeaderSize = 12;

    /// <summary>Los dos bits altos a 1 en el byte de longitud marcan un puntero de compresión.</summary>
    private const byte CompressionPointerMask = 0xC0;

    /// <summary>Bit alto del campo de clase: cache-flush en registros, unicast-response en preguntas.</summary>
    private const ushort ClassHighBit = 0x8000;

    /// <summary>Un nombre DNS no puede pasar de 255 bytes según el RFC 1035.</summary>
    private const int MaxNameLength = 255;

    /// <summary>Tope de saltos de compresión, para cortar punteros que se apuntan en bucle.</summary>
    private const int MaxCompressionJumps = 64;

    public static DnsMessage Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new DnsFormatException($"Mensaje DNS demasiado corto: {data.Length} bytes.");
        }

        var header = DnsHeader.FromFlags(
            BinaryPrimitives.ReadUInt16BigEndian(data),
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[10..]));

        var position = HeaderSize;

        var questions = new List<DnsQuestion>(header.QuestionCount);
        for (var i = 0; i < header.QuestionCount; i++)
        {
            questions.Add(ReadQuestion(data, ref position));
        }

        var answers = ReadRecords(data, ref position, header.AnswerCount);
        var authorities = ReadRecords(data, ref position, header.AuthorityCount);
        var additionals = ReadRecords(data, ref position, header.AdditionalCount);

        return new DnsMessage(header, questions, answers, authorities, additionals);
    }

    private static List<DnsResourceRecord> ReadRecords(
        ReadOnlySpan<byte> data,
        ref int position,
        int count)
    {
        var records = new List<DnsResourceRecord>(count);
        for (var i = 0; i < count; i++)
        {
            records.Add(ReadRecord(data, ref position));
        }

        return records;
    }

    private static DnsQuestion ReadQuestion(ReadOnlySpan<byte> data, ref int position)
    {
        var name = ReadName(data, ref position);
        EnsureAvailable(data, position, 4);

        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        var classField = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
        position += 4;

        return new DnsQuestion(name, type, (classField & ClassHighBit) != 0);
    }

    private static DnsResourceRecord ReadRecord(ReadOnlySpan<byte> data, ref int position)
    {
        var name = ReadName(data, ref position);
        EnsureAvailable(data, position, 10);

        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        var classField = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
        var ttlSeconds = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
        var dataLength = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 8)..]);
        position += 10;

        EnsureAvailable(data, position, dataLength);
        var recordDataStart = position;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var cacheFlush = (classField & ClassHighBit) != 0;

        // La posición avanza siempre por RDLENGTH, no por lo que consuma el parser del tipo
        // concreto: así un registro con datos inesperados no descoloca el resto del mensaje.
        position += dataLength;

        return type switch
        {
            DnsRecordType.Ptr => ReadPtr(data, recordDataStart, name, ttl, cacheFlush),
            DnsRecordType.Srv => ReadSrv(data, recordDataStart, dataLength, name, ttl, cacheFlush),
            DnsRecordType.Txt => ReadTxt(data, recordDataStart, dataLength, name, ttl, cacheFlush),
            DnsRecordType.A or DnsRecordType.Aaaa =>
                ReadAddress(data, recordDataStart, dataLength, type, name, ttl, cacheFlush),
            _ => new UnknownRecord(
                name, type, data.Slice(recordDataStart, dataLength).ToArray(), ttl, cacheFlush),
        };
    }

    private static PtrRecord ReadPtr(
        ReadOnlySpan<byte> data, int start, string name, TimeSpan ttl, bool cacheFlush)
    {
        var position = start;
        return new PtrRecord(name, ReadName(data, ref position), ttl, cacheFlush);
    }

    private static DnsResourceRecord ReadSrv(
        ReadOnlySpan<byte> data, int start, int length, string name, TimeSpan ttl, bool cacheFlush)
    {
        if (length < 7)
        {
            return new UnknownRecord(
                name, DnsRecordType.Srv, data.Slice(start, length).ToArray(), ttl, cacheFlush);
        }

        var priority = BinaryPrimitives.ReadUInt16BigEndian(data[start..]);
        var weight = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 2)..]);
        var port = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 4)..]);

        var position = start + 6;
        var target = ReadName(data, ref position);

        return new SrvRecord(name, target, port, ttl, priority, weight, cacheFlush);
    }

    private static TxtRecord ReadTxt(
        ReadOnlySpan<byte> data, int start, int length, string name, TimeSpan ttl, bool cacheFlush)
    {
        // RDATA de TXT: secuencia de cadenas, cada una precedida de su longitud en un byte.
        var strings = new List<string>();
        var position = start;
        var end = start + length;

        while (position < end)
        {
            int stringLength = data[position];
            position++;

            if (position + stringLength > end)
            {
                break;   // cadena truncada: se conserva lo válido en vez de descartar el registro
            }

            strings.Add(Encoding.UTF8.GetString(data.Slice(position, stringLength)));
            position += stringLength;
        }

        return new TxtRecord(name, strings, ttl, cacheFlush);
    }

    private static DnsResourceRecord ReadAddress(
        ReadOnlySpan<byte> data,
        int start,
        int length,
        DnsRecordType type,
        string name,
        TimeSpan ttl,
        bool cacheFlush)
    {
        var expected = type == DnsRecordType.A ? 4 : 16;
        if (length != expected)
        {
            return new UnknownRecord(name, type, data.Slice(start, length).ToArray(), ttl, cacheFlush);
        }

        return new AddressRecord(
            name, new IPAddress(data.Slice(start, length)), ttl, cacheFlush);
    }

    /// <summary>
    /// Lee un nombre DNS, siguiendo los punteros de compresión.
    /// </summary>
    /// <remarks>
    /// Al seguir un puntero, <paramref name="position"/> se deja justo después del puntero, no en
    /// el destino: el nombre comprimido ocupa solo dos bytes en el flujo.
    /// </remarks>
    private static string ReadName(ReadOnlySpan<byte> data, ref int position)
    {
        var labels = new List<string>();
        var jumps = 0;
        var totalLength = 0;
        var current = position;
        var positionAfterName = -1;

        while (true)
        {
            EnsureAvailable(data, current, 1);
            var lengthByte = data[current];

            if (lengthByte == 0)
            {
                current++;
                break;
            }

            if ((lengthByte & CompressionPointerMask) == CompressionPointerMask)
            {
                EnsureAvailable(data, current, 2);

                if (++jumps > MaxCompressionJumps)
                {
                    throw new DnsFormatException(
                        "Punteros de compresión en bucle o excesivamente encadenados.");
                }

                // El offset son los 14 bits bajos del par de bytes.
                var offset = BinaryPrimitives.ReadUInt16BigEndian(data[current..]) & 0x3FFF;

                if (offset >= data.Length)
                {
                    throw new DnsFormatException($"Puntero de compresión fuera del mensaje: {offset}.");
                }

                // Solo el primer salto determina dónde continúa la lectura del flujo.
                positionAfterName = positionAfterName < 0 ? current + 2 : positionAfterName;
                current = offset;
                continue;
            }

            if ((lengthByte & CompressionPointerMask) != 0)
            {
                throw new DnsFormatException($"Tipo de etiqueta DNS no soportado: 0x{lengthByte:X2}.");
            }

            current++;
            EnsureAvailable(data, current, lengthByte);

            totalLength += lengthByte + 1;
            if (totalLength > MaxNameLength)
            {
                throw new DnsFormatException($"Nombre DNS de más de {MaxNameLength} bytes.");
            }

            labels.Add(Encoding.UTF8.GetString(data.Slice(current, lengthByte)));
            current += lengthByte;
        }

        position = positionAfterName < 0 ? current : positionAfterName;
        return string.Join('.', labels);
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int position, int length)
    {
        if (position < 0 || length < 0 || position + length > data.Length)
        {
            throw new DnsFormatException(
                $"Lectura fuera de los límites: {length} bytes en el offset {position} " +
                $"de un mensaje de {data.Length}.");
        }
    }
}
