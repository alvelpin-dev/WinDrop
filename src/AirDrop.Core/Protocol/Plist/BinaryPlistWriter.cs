using System.Buffers.Binary;
using System.Text;

namespace AirDrop.Core.Protocol.Plist;

/// <summary>
/// Escritor del formato binary property list de Apple (<c>bplist00</c>).
/// </summary>
/// <remarks>
/// <para>El proceso tiene dos fases:</para>
/// <list type="number">
///   <item>Aplanar el árbol de valores en una tabla de objetos y asignar un índice a cada uno.</item>
///   <item>Serializar cada objeto anotando su offset, y cerrar con la tabla de offsets y el trailer.</item>
/// </list>
/// <para>
/// Los escalares se deduplican: en los mensajes de AirDrop las mismas claves se repiten en cada
/// elemento del array <c>Files</c>, y deduplicar reduce el tamaño de forma apreciable. Los
/// contenedores no se deduplican porque comparar su estructura costaría más de lo que ahorra.
/// </para>
/// </remarks>
public static class BinaryPlistWriter
{
    private static ReadOnlySpan<byte> Magic => "bplist00"u8;

    /// <summary>Serializa un valor como plist binario.</summary>
    public static byte[] Write(PlistValue root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var objects = new List<PlistValue>();
        var scalarIndex = new Dictionary<PlistValue, int>();
        var assigned = new Dictionary<PlistValue, int>(ReferenceEqualityComparer.Instance);
        Flatten(root, objects, scalarIndex, assigned);

        var objectRefSize = SizeForValue((ulong)(objects.Count - 1));

        using var stream = new MemoryStream();
        stream.Write(Magic);

        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = stream.Position;
            WriteObject(stream, objects[i], assigned, scalarIndex, objectRefSize);
        }

        var offsetTableOffset = stream.Position;
        var offsetIntSize = SizeForValue((ulong)offsetTableOffset);
        foreach (var offset in offsets)
        {
            WriteBigEndianUnsigned(stream, (ulong)offset, offsetIntSize);
        }

        WriteTrailer(stream, offsetIntSize, objectRefSize, objects.Count, offsetTableOffset);
        return stream.ToArray();
    }

    /// <summary>
    /// Recorre el árbol asignando un índice a cada objeto.
    /// </summary>
    /// <remarks>
    /// <paramref name="assigned"/> mapea cada instancia concreta a su índice y usa igualdad por
    /// referencia; <paramref name="scalarIndex"/> deduplica escalares por valor. Se necesitan los
    /// dos: el primero para resolver referencias al serializar, el segundo para reutilizar objetos.
    /// </remarks>
    private static int Flatten(
        PlistValue value,
        List<PlistValue> objects,
        Dictionary<PlistValue, int> scalarIndex,
        Dictionary<PlistValue, int> assigned)
    {
        if (assigned.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var isScalar = value is not (PlistArray or PlistDictionary);
        if (isScalar && scalarIndex.TryGetValue(value, out var deduplicated))
        {
            assigned[value] = deduplicated;
            return deduplicated;
        }

        var index = objects.Count;
        objects.Add(value);
        assigned[value] = index;

        if (isScalar)
        {
            scalarIndex[value] = index;
            return index;
        }

        switch (value)
        {
            case PlistArray array:
                foreach (var item in array.Items)
                {
                    Flatten(item, objects, scalarIndex, assigned);
                }

                break;

            case PlistDictionary dictionary:
                // Las claves van antes que los valores, igual que en el fichero.
                foreach (var key in dictionary.Entries.Keys)
                {
                    Flatten(new PlistString(key), objects, scalarIndex, assigned);
                }

                foreach (var entry in dictionary.Entries.Values)
                {
                    Flatten(entry, objects, scalarIndex, assigned);
                }

                break;
        }

        return index;
    }

    private static void WriteObject(
        Stream stream,
        PlistValue value,
        Dictionary<PlistValue, int> assigned,
        Dictionary<PlistValue, int> scalarIndex,
        int objectRefSize)
    {
        switch (value)
        {
            case PlistBoolean b:
                stream.WriteByte(b.Value ? (byte)0x09 : (byte)0x08);
                break;

            case PlistInteger i:
                WriteInteger(stream, i.Value);
                break;

            case PlistReal r:
                stream.WriteByte(0x23);
                WriteDoubleBigEndian(stream, r.Value);
                break;

            case PlistDate d:
                stream.WriteByte(0x33);
                WriteDoubleBigEndian(stream, d.ToAppleSeconds());
                break;

            case PlistData data:
                WriteMarkerWithLength(stream, 0x40, data.Value.Length);
                stream.Write(data.Value);
                break;

            case PlistString s:
                WriteString(stream, s.Value);
                break;

            case PlistArray array:
                WriteMarkerWithLength(stream, 0xA0, array.Count);
                foreach (var item in array.Items)
                {
                    WriteReference(stream, item, assigned, scalarIndex, objectRefSize);
                }

                break;

            case PlistDictionary dictionary:
                WriteMarkerWithLength(stream, 0xD0, dictionary.Count);
                foreach (var key in dictionary.Entries.Keys)
                {
                    WriteReference(stream, new PlistString(key), assigned, scalarIndex, objectRefSize);
                }

                foreach (var entry in dictionary.Entries.Values)
                {
                    WriteReference(stream, entry, assigned, scalarIndex, objectRefSize);
                }

                break;

            default:
                throw new PlistFormatException($"Tipo de plist no serializable: {value.GetType().Name}.");
        }
    }

    private static void WriteReference(
        Stream stream,
        PlistValue value,
        Dictionary<PlistValue, int> assigned,
        Dictionary<PlistValue, int> scalarIndex,
        int objectRefSize)
    {
        // Las claves de diccionario se reconstruyen como PlistString nuevos en cada pasada, así que
        // la búsqueda por referencia falla y hay que caer en la tabla de escalares deduplicados.
        if (!assigned.TryGetValue(value, out var index) && !scalarIndex.TryGetValue(value, out index))
        {
            throw new PlistFormatException("Referencia a un objeto que no se registró al aplanar.");
        }

        WriteBigEndianUnsigned(stream, (ulong)index, objectRefSize);
    }

    private static void WriteInteger(Stream stream, long value)
    {
        // Los negativos se codifican siempre en 8 bytes: en este formato los enteros de 1, 2 y 4
        // bytes se interpretan como sin signo al leerlos.
        if (value < 0)
        {
            stream.WriteByte(0x13);
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            stream.Write(buffer);
            return;
        }

        if (value <= byte.MaxValue)
        {
            stream.WriteByte(0x10);
            stream.WriteByte((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            stream.WriteByte(0x11);
            WriteBigEndianUnsigned(stream, (ulong)value, 2);
        }
        else if (value <= uint.MaxValue)
        {
            stream.WriteByte(0x12);
            WriteBigEndianUnsigned(stream, (ulong)value, 4);
        }
        else
        {
            stream.WriteByte(0x13);
            WriteBigEndianUnsigned(stream, (ulong)value, 8);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        // ASCII cuando se puede (la mayoría de claves del protocolo); UTF-16BE para nombres de
        // dispositivo o de fichero con acentos, emoji, etc.
        if (Ascii.IsValid(value))
        {
            WriteMarkerWithLength(stream, 0x50, value.Length);
            Span<byte> buffer = value.Length <= 256 ? stackalloc byte[value.Length] : new byte[value.Length];
            Ascii.FromUtf16(value, buffer, out _);
            stream.Write(buffer);
            return;
        }

        // La longitud se cuenta en unidades de código UTF-16, no en bytes.
        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        WriteMarkerWithLength(stream, 0x60, bytes.Length / 2);
        stream.Write(bytes);
    }

    /// <summary>
    /// Escribe el marcador de tipo. Si la longitud no cabe en el nibble bajo se emite 0xF y la
    /// longitud real va a continuación como un objeto entero embebido.
    /// </summary>
    private static void WriteMarkerWithLength(Stream stream, int marker, int length)
    {
        if (length < 0xF)
        {
            stream.WriteByte((byte)(marker | length));
            return;
        }

        stream.WriteByte((byte)(marker | 0x0F));
        WriteInteger(stream, length);
    }

    private static void WriteTrailer(
        Stream stream,
        int offsetIntSize,
        int objectRefSize,
        int numObjects,
        long offsetTableOffset)
    {
        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetIntSize;
        trailer[7] = (byte)objectRefSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)numObjects);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);   // el objeto raíz es siempre el 0
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTableOffset);
        stream.Write(trailer);
    }

    private static void WriteDoubleBigEndian(Stream stream, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteBigEndianUnsigned(Stream stream, ulong value, int byteCount)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer[(8 - byteCount)..]);
    }

    /// <summary>Número mínimo de bytes (1, 2, 4 u 8) necesarios para representar un valor.</summary>
    private static int SizeForValue(ulong value) => value switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= uint.MaxValue => 4,
        _ => 8,
    };
}
