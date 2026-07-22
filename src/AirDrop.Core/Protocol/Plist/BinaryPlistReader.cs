using System.Buffers.Binary;
using System.Text;

namespace AirDrop.Core.Protocol.Plist;

/// <summary>
/// Lector del formato binary property list de Apple (<c>bplist00</c>).
/// </summary>
/// <remarks>
/// <para>Estructura del fichero:</para>
/// <code>
///   [0..8)      cabecera mágica "bplist00"
///   [8..T)      objetos codificados
///   [T..E)      tabla de offsets: NumObjects entradas de OffsetIntSize bytes
///   [E-32..E)   trailer de 32 bytes
/// </code>
/// <para>
/// Todos los enteros multibyte van en big-endian.
/// </para>
/// <para>
/// El lector es defensivo por diseño: los plists llegan por la red desde dispositivos no
/// confiables, así que cada offset y cada referencia se valida contra los límites del buffer, y
/// la recursión se limita para que un plist malicioso no pueda provocar un desbordamiento de pila
/// ni un bucle infinito por referencias cíclicas.
/// </para>
/// </remarks>
public static class BinaryPlistReader
{
    private static ReadOnlySpan<byte> Magic => "bplist00"u8;

    private const int TrailerSize = 32;

    /// <summary>Profundidad máxima de anidamiento. Los plists de AirDrop no pasan de 3-4 niveles.</summary>
    private const int MaxDepth = 32;

    /// <summary>Lee un plist binario completo y devuelve su objeto raíz.</summary>
    /// <exception cref="PlistFormatException">El contenido no es un bplist00 válido.</exception>
    public static PlistValue Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + TrailerSize)
        {
            throw new PlistFormatException(
                $"Demasiado corto para ser un bplist: {data.Length} bytes.");
        }

        if (!data[..Magic.Length].SequenceEqual(Magic))
        {
            throw new PlistFormatException("Falta la cabecera mágica 'bplist00'.");
        }

        var trailer = data[^TrailerSize..];

        // Trailer: 5 bytes sin usar, 1 byte sortVersion, luego los tamaños y contadores.
        int offsetIntSize = trailer[6];
        int objectRefSize = trailer[7];
        var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        if (offsetIntSize is < 1 or > 8)
        {
            throw new PlistFormatException($"OffsetIntSize inválido: {offsetIntSize}.");
        }

        if (objectRefSize is < 1 or > 8)
        {
            throw new PlistFormatException($"ObjectRefSize inválido: {objectRefSize}.");
        }

        // numObjects viene del propio fichero: hay que acotarlo antes de usarlo para dimensionar nada.
        if (numObjects == 0 || numObjects > (ulong)data.Length)
        {
            throw new PlistFormatException($"NumObjects inválido: {numObjects}.");
        }

        if (topObject >= numObjects)
        {
            throw new PlistFormatException(
                $"TopObject {topObject} fuera de rango (NumObjects={numObjects}).");
        }

        var tableEnd = offsetTableOffset + (numObjects * (ulong)offsetIntSize);
        if (offsetTableOffset < (ulong)Magic.Length || tableEnd > (ulong)(data.Length - TrailerSize))
        {
            throw new PlistFormatException("La tabla de offsets se sale del fichero.");
        }

        var offsets = new long[numObjects];
        var table = data[(int)offsetTableOffset..];
        for (var i = 0UL; i < numObjects; i++)
        {
            var offset = ReadBigEndianUnsigned(table[(int)(i * (ulong)offsetIntSize)..], offsetIntSize);
            if (offset < (ulong)Magic.Length || offset >= offsetTableOffset)
            {
                throw new PlistFormatException($"Offset del objeto {i} fuera de rango: {offset}.");
            }

            offsets[i] = (long)offset;
        }

        var context = new ReadContext(data, offsets, objectRefSize);
        return ReadObject(context, (long)topObject, depth: 0);
    }

    /// <summary>Lee un plist binario desde un array de bytes.</summary>
    public static PlistValue Read(byte[] data) => Read(data.AsSpan());

    /// <summary>Lee un plist binario cuyo objeto raíz debe ser un diccionario.</summary>
    /// <remarks>Todos los mensajes de AirDrop tienen un diccionario en la raíz.</remarks>
    /// <exception cref="PlistFormatException">La raíz no es un diccionario.</exception>
    public static PlistDictionary ReadDictionary(ReadOnlySpan<byte> data) =>
        Read(data) as PlistDictionary
        ?? throw new PlistFormatException("Se esperaba un diccionario en la raíz del plist.");

    private readonly ref struct ReadContext(ReadOnlySpan<byte> data, long[] offsets, int objectRefSize)
    {
        public readonly ReadOnlySpan<byte> Data = data;
        public readonly long[] Offsets = offsets;
        public readonly int ObjectRefSize = objectRefSize;
    }

    private static PlistValue ReadObject(in ReadContext context, long index, int depth)
    {
        if (depth > MaxDepth)
        {
            // Protege contra plists con referencias cíclicas, que la tabla de offsets permite expresar.
            throw new PlistFormatException($"Anidamiento superior al máximo permitido ({MaxDepth}).");
        }

        if (index < 0 || index >= context.Offsets.Length)
        {
            throw new PlistFormatException($"Referencia a objeto fuera de rango: {index}.");
        }

        var data = context.Data;
        var position = (int)context.Offsets[index];
        var marker = data[position];
        var objectType = marker & 0xF0;
        var nibble = marker & 0x0F;
        position++;

        return objectType switch
        {
            0x00 => ReadSingleton(marker),
            0x10 => ReadInteger(data, position, nibble),
            0x20 => ReadReal(data, position, nibble),
            0x30 => ReadDate(data, position, nibble),
            0x40 => ReadData(context, ref position, nibble),
            0x50 => ReadAsciiString(context, ref position, nibble),
            0x60 => ReadUtf16String(context, ref position, nibble),
            0x70 => ReadUtf8String(context, ref position, nibble),
            0xA0 => ReadArray(context, ref position, nibble, depth),
            0xD0 => ReadDictionary(context, ref position, nibble, depth),
            _ => throw new PlistFormatException(
                $"Tipo de objeto no soportado: 0x{marker:X2} en el offset {context.Offsets[index]}."),
        };
    }

    private static PlistValue ReadSingleton(byte marker) => marker switch
    {
        0x08 => new PlistBoolean(false),
        0x09 => new PlistBoolean(true),
        _ => throw new PlistFormatException($"Marcador singleton no soportado: 0x{marker:X2}."),
    };

    private static PlistValue ReadInteger(ReadOnlySpan<byte> data, int position, int nibble)
    {
        // El nibble es el log2 del número de bytes: 0→1, 1→2, 2→4, 3→8, 4→16.
        var length = 1 << nibble;
        EnsureAvailable(data, position, length);

        // Los enteros de 8 bytes son con signo; los más cortos, sin signo. Los de 16 bytes existen
        // en la especificación pero no aparecen en AirDrop, así que se toman los 8 bytes bajos.
        return length switch
        {
            1 => new PlistInteger(data[position]),
            2 => new PlistInteger(BinaryPrimitives.ReadUInt16BigEndian(data[position..])),
            4 => new PlistInteger(BinaryPrimitives.ReadUInt32BigEndian(data[position..])),
            8 => new PlistInteger(BinaryPrimitives.ReadInt64BigEndian(data[position..])),
            16 => new PlistInteger(BinaryPrimitives.ReadInt64BigEndian(data[(position + 8)..])),
            _ => throw new PlistFormatException($"Longitud de entero inválida: {length}."),
        };
    }

    private static PlistValue ReadReal(ReadOnlySpan<byte> data, int position, int nibble)
    {
        var length = 1 << nibble;
        EnsureAvailable(data, position, length);

        return length switch
        {
            4 => new PlistReal(BinaryPrimitives.ReadSingleBigEndian(data[position..])),
            8 => new PlistReal(BinaryPrimitives.ReadDoubleBigEndian(data[position..])),
            _ => throw new PlistFormatException($"Longitud de real inválida: {length}."),
        };
    }

    private static PlistValue ReadDate(ReadOnlySpan<byte> data, int position, int nibble)
    {
        if (nibble != 3)
        {
            throw new PlistFormatException($"Marcador de fecha inválido: 0x3{nibble:X}.");
        }

        EnsureAvailable(data, position, 8);
        return PlistDate.FromAppleSeconds(BinaryPrimitives.ReadDoubleBigEndian(data[position..]));
    }

    private static PlistValue ReadData(in ReadContext context, ref int position, int nibble)
    {
        var length = ReadLength(context, ref position, nibble);
        EnsureAvailable(context.Data, position, length);
        return new PlistData(context.Data.Slice(position, length).ToArray());
    }

    private static PlistValue ReadAsciiString(in ReadContext context, ref int position, int nibble)
    {
        var length = ReadLength(context, ref position, nibble);
        EnsureAvailable(context.Data, position, length);
        return new PlistString(Encoding.ASCII.GetString(context.Data.Slice(position, length)));
    }

    private static PlistValue ReadUtf16String(in ReadContext context, ref int position, int nibble)
    {
        // El nibble cuenta unidades de código UTF-16, no bytes.
        var units = ReadLength(context, ref position, nibble);
        var byteLength = units * 2;
        EnsureAvailable(context.Data, position, byteLength);
        return new PlistString(
            Encoding.BigEndianUnicode.GetString(context.Data.Slice(position, byteLength)));
    }

    private static PlistValue ReadUtf8String(in ReadContext context, ref int position, int nibble)
    {
        var length = ReadLength(context, ref position, nibble);
        EnsureAvailable(context.Data, position, length);
        return new PlistString(Encoding.UTF8.GetString(context.Data.Slice(position, length)));
    }

    private static PlistValue ReadArray(in ReadContext context, ref int position, int nibble, int depth)
    {
        var count = ReadLength(context, ref position, nibble);
        var refs = ReadReferences(context, ref position, count);

        var items = new PlistValue[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = ReadObject(context, refs[i], depth + 1);
        }

        return new PlistArray(items);
    }

    private static PlistValue ReadDictionary(in ReadContext context, ref int position, int nibble, int depth)
    {
        var count = ReadLength(context, ref position, nibble);

        // Primero todas las referencias a claves, después todas las de valores.
        var keyRefs = ReadReferences(context, ref position, count);
        var valueRefs = ReadReferences(context, ref position, count);

        var entries = new Dictionary<string, PlistValue>(count);
        for (var i = 0; i < count; i++)
        {
            if (ReadObject(context, keyRefs[i], depth + 1) is not PlistString key)
            {
                throw new PlistFormatException("Las claves de un diccionario deben ser cadenas.");
            }

            entries[key.Value] = ReadObject(context, valueRefs[i], depth + 1);
        }

        return new PlistDictionary(entries);
    }

    /// <summary>
    /// Lee la longitud de un objeto de tamaño variable. El nibble 0xF indica que la longitud real
    /// viene a continuación como un objeto entero embebido.
    /// </summary>
    private static int ReadLength(in ReadContext context, ref int position, int nibble)
    {
        if (nibble != 0xF)
        {
            return nibble;
        }

        var data = context.Data;
        EnsureAvailable(data, position, 1);

        var marker = data[position];
        if ((marker & 0xF0) != 0x10)
        {
            throw new PlistFormatException(
                $"Se esperaba un entero de longitud, se encontró 0x{marker:X2}.");
        }

        position++;
        var intLength = 1 << (marker & 0x0F);
        EnsureAvailable(data, position, intLength);

        var value = ReadBigEndianUnsigned(data[position..], intLength);
        position += intLength;

        // La longitud declarada no puede exceder el buffer: evita asignaciones desmesuradas
        // provocadas por un plist manipulado.
        if (value > (ulong)data.Length)
        {
            throw new PlistFormatException($"Longitud declarada desmesurada: {value}.");
        }

        return (int)value;
    }

    private static long[] ReadReferences(in ReadContext context, ref int position, int count)
    {
        var size = context.ObjectRefSize;
        EnsureAvailable(context.Data, position, count * size);

        var refs = new long[count];
        for (var i = 0; i < count; i++)
        {
            refs[i] = (long)ReadBigEndianUnsigned(context.Data[(position + (i * size))..], size);
        }

        position += count * size;
        return refs;
    }

    private static ulong ReadBigEndianUnsigned(ReadOnlySpan<byte> source, int byteCount)
    {
        if (source.Length < byteCount)
        {
            throw new PlistFormatException("Lectura fuera de los límites del plist.");
        }

        var value = 0UL;
        for (var i = 0; i < byteCount; i++)
        {
            value = (value << 8) | source[i];
        }

        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int position, int length)
    {
        if (position < 0 || length < 0 || position + length > data.Length)
        {
            throw new PlistFormatException(
                $"Lectura fuera de los límites: se pidieron {length} bytes en el offset {position} " +
                $"de un buffer de {data.Length}.");
        }
    }
}

/// <summary>Error de formato al leer o escribir un property list.</summary>
public sealed class PlistFormatException(string message) : Exception(message);
