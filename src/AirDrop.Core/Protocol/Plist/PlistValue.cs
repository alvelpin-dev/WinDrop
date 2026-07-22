namespace AirDrop.Core.Protocol.Plist;

/// <summary>
/// Un valor dentro de un property list de Apple.
/// </summary>
/// <remarks>
/// Todos los mensajes del protocolo AirDrop (<c>/Discover</c>, <c>/Ask</c>, <c>/Upload</c>) viajan
/// como binary property lists (<c>bplist00</c>). Este modelo cubre el subconjunto de tipos que
/// aparecen en el protocolo; deliberadamente NO implementa <c>set</c> ni <c>UID</c>, que pertenecen
/// al dominio de NSKeyedArchiver y no se han observado en AirDrop.
/// </remarks>
public abstract record PlistValue
{
    // Conversiones implícitas: construir un plist a mano debe leerse como código normal,
    // no como una cascada de constructores.
    public static implicit operator PlistValue(string value) => new PlistString(value);
    public static implicit operator PlistValue(long value) => new PlistInteger(value);
    public static implicit operator PlistValue(int value) => new PlistInteger(value);
    public static implicit operator PlistValue(bool value) => new PlistBoolean(value);
    public static implicit operator PlistValue(double value) => new PlistReal(value);
    public static implicit operator PlistValue(byte[] value) => new PlistData(value);
}

public sealed record PlistString(string Value) : PlistValue;

public sealed record PlistInteger(long Value) : PlistValue;

public sealed record PlistReal(double Value) : PlistValue;

public sealed record PlistBoolean(bool Value) : PlistValue;

/// <summary>Datos binarios opacos: <c>SenderRecordData</c> (PKCS#7), <c>FileIcon</c> (JPEG 2000)…</summary>
public sealed record PlistData(byte[] Value) : PlistValue
{
    public bool Equals(PlistData? other) =>
        other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Fecha de Apple: segundos en coma flotante desde la época de referencia de Core Foundation
/// (2001-01-01T00:00:00Z), no desde la época Unix.
/// </summary>
public sealed record PlistDate(DateTimeOffset Value) : PlistValue
{
    /// <summary>Época de referencia de Core Foundation.</summary>
    public static readonly DateTimeOffset AppleEpoch =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static PlistDate FromAppleSeconds(double seconds) =>
        new(AppleEpoch.AddSeconds(seconds));

    public double ToAppleSeconds() => (Value - AppleEpoch).TotalSeconds;
}

public sealed record PlistArray(IReadOnlyList<PlistValue> Items) : PlistValue
{
    public PlistArray(params PlistValue[] items) : this((IReadOnlyList<PlistValue>)items) { }

    public int Count => Items.Count;

    public PlistValue this[int index] => Items[index];

    public bool Equals(PlistArray? other) =>
        other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

public sealed record PlistDictionary(IReadOnlyDictionary<string, PlistValue> Entries) : PlistValue
{
    public PlistDictionary() : this(new Dictionary<string, PlistValue>()) { }

    public int Count => Entries.Count;

    public PlistValue this[string key] => Entries[key];

    public bool TryGetValue(string key, out PlistValue value) => Entries.TryGetValue(key, out value!);

    /// <summary>Lee una cadena, o <c>null</c> si la clave falta o no es una cadena.</summary>
    public string? GetString(string key) =>
        Entries.TryGetValue(key, out var v) && v is PlistString s ? s.Value : null;

    /// <summary>Lee un entero, o <c>null</c> si la clave falta o no es un entero.</summary>
    public long? GetInteger(string key) =>
        Entries.TryGetValue(key, out var v) && v is PlistInteger i ? i.Value : null;

    /// <summary>Lee un booleano, o <c>null</c> si la clave falta o no es un booleano.</summary>
    public bool? GetBoolean(string key) =>
        Entries.TryGetValue(key, out var v) && v is PlistBoolean b ? b.Value : null;

    /// <summary>Lee datos binarios, o <c>null</c> si la clave falta o no son datos.</summary>
    public byte[]? GetData(string key) =>
        Entries.TryGetValue(key, out var v) && v is PlistData d ? d.Value : null;

    /// <summary>Lee un array, o <c>null</c> si la clave falta o no es un array.</summary>
    public PlistArray? GetArray(string key) =>
        Entries.TryGetValue(key, out var v) ? v as PlistArray : null;

    /// <summary>Lee un diccionario anidado, o <c>null</c> si la clave falta o no es un diccionario.</summary>
    public PlistDictionary? GetDictionary(string key) =>
        Entries.TryGetValue(key, out var v) ? v as PlistDictionary : null;

    public bool Equals(PlistDictionary? other)
    {
        if (other is null || Entries.Count != other.Entries.Count)
        {
            return false;
        }

        foreach (var (key, value) in Entries)
        {
            if (!other.Entries.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        // Orden-independiente: los diccionarios no garantizan orden de iteración.
        var hash = 0;
        foreach (var (key, value) in Entries)
        {
            hash ^= HashCode.Combine(key, value);
        }

        return hash;
    }
}

/// <summary>Constructor fluido para armar diccionarios de plist de forma legible.</summary>
public sealed class PlistDictionaryBuilder
{
    private readonly Dictionary<string, PlistValue> _entries = [];

    public PlistDictionaryBuilder Set(string key, PlistValue value)
    {
        _entries[key] = value;
        return this;
    }

    /// <summary>Añade la clave solo si el valor no es nulo. Útil para campos opcionales del protocolo.</summary>
    public PlistDictionaryBuilder SetIfNotNull(string key, PlistValue? value)
    {
        if (value is not null)
        {
            _entries[key] = value;
        }

        return this;
    }

    public PlistDictionary Build() => new(_entries);
}
