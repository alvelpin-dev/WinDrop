using System.Net;

namespace AirDrop.Discovery.Dns;

/// <summary>Tipos de registro DNS usados en DNS-SD.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,

    /// <summary>Negative response. Se usa en mDNS para declarar qué tipos NO existen para un nombre.</summary>
    Nsec = 47,

    Any = 255,
}

/// <summary>Clases DNS. En mDNS solo se usa <see cref="Internet"/>, con el bit alto como bandera.</summary>
public enum DnsRecordClass : ushort
{
    Internet = 1,
}

/// <summary>
/// Cabecera de un mensaje DNS.
/// </summary>
/// <remarks>
/// En mDNS el identificador es normalmente 0: las respuestas no se emparejan con la pregunta por
/// ID como en el DNS unicast, sino por el nombre del registro.
/// </remarks>
public sealed record DnsHeader(
    ushort Id,
    bool IsResponse,
    bool IsAuthoritative,
    ushort QuestionCount,
    ushort AnswerCount,
    ushort AuthorityCount,
    ushort AdditionalCount)
{
    /// <summary>Bit QR: distingue consulta de respuesta.</summary>
    private const ushort ResponseFlag = 0x8000;

    /// <summary>Bit AA: respuesta autoritativa. Siempre activo en las respuestas mDNS.</summary>
    private const ushort AuthoritativeFlag = 0x0400;

    public ushort Flags =>
        (ushort)((IsResponse ? ResponseFlag : 0) | (IsAuthoritative ? AuthoritativeFlag : 0));

    public static DnsHeader FromFlags(
        ushort id,
        ushort flags,
        ushort questionCount,
        ushort answerCount,
        ushort authorityCount,
        ushort additionalCount) =>
        new(
            id,
            (flags & ResponseFlag) != 0,
            (flags & AuthoritativeFlag) != 0,
            questionCount,
            answerCount,
            authorityCount,
            additionalCount);
}

/// <summary>Una pregunta dentro de un mensaje DNS.</summary>
/// <param name="Name">Nombre consultado, p. ej. <c>_airdrop._tcp.local</c>.</param>
/// <param name="Type">Tipo de registro solicitado.</param>
/// <param name="WantsUnicastResponse">
/// Bit QU de mDNS: el consultante pide que se le responda por unicast en lugar de por multicast.
/// Se usa en la primera consulta tras arrancar, para no inundar la red.
/// </param>
public sealed record DnsQuestion(
    string Name,
    DnsRecordType Type,
    bool WantsUnicastResponse = false);

/// <summary>Registro de recurso DNS.</summary>
/// <param name="Name">Nombre al que pertenece el registro.</param>
/// <param name="Type">Tipo de registro.</param>
/// <param name="Ttl">Tiempo de vida. Un TTL de cero indica que el registro deja de ser válido.</param>
/// <param name="CacheFlush">
/// Bit de cache-flush de mDNS: indica al receptor que descarte los registros previos de este
/// nombre y tipo. Es lo que permite anunciar un cambio de nombre o de IP sin esperar al TTL.
/// </param>
public abstract record DnsResourceRecord(
    string Name,
    DnsRecordType Type,
    TimeSpan Ttl,
    bool CacheFlush = false);

/// <summary>Registro PTR: apunta de un tipo de servicio a una instancia concreta.</summary>
/// <remarks>
/// En DNS-SD es el registro de entrada: <c>_airdrop._tcp.local</c> → <c>MiPC._airdrop._tcp.local</c>.
/// </remarks>
public sealed record PtrRecord(
    string Name,
    string Target,
    TimeSpan Ttl,
    bool CacheFlush = false)
    : DnsResourceRecord(Name, DnsRecordType.Ptr, Ttl, CacheFlush);

/// <summary>Registro SRV: dice en qué host y puerto vive una instancia de servicio.</summary>
public sealed record SrvRecord(
    string Name,
    string Target,
    ushort Port,
    TimeSpan Ttl,
    ushort Priority = 0,
    ushort Weight = 0,
    bool CacheFlush = true)
    : DnsResourceRecord(Name, DnsRecordType.Srv, Ttl, CacheFlush);

/// <summary>Registro TXT: pares clave-valor con los metadatos del servicio.</summary>
/// <remarks>
/// En AirDrop transporta la clave <c>flags</c> con las capacidades anunciadas
/// (ver <see cref="Core.Protocol.AirDropFlags"/>).
/// </remarks>
public sealed record TxtRecord(
    string Name,
    IReadOnlyList<string> Strings,
    TimeSpan Ttl,
    bool CacheFlush = true)
    : DnsResourceRecord(Name, DnsRecordType.Txt, Ttl, CacheFlush)
{
    /// <summary>Construye un TXT a partir de pares clave-valor.</summary>
    public static TxtRecord FromPairs(
        string name,
        IReadOnlyDictionary<string, string> pairs,
        TimeSpan ttl) =>
        new(name, [.. pairs.Select(p => $"{p.Key}={p.Value}")], ttl);

    /// <summary>
    /// Interpreta las cadenas como pares clave-valor.
    /// </summary>
    /// <remarks>
    /// Una cadena sin '=' es una clave con valor vacío, según la especificación de DNS-SD.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToPairs()
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Strings)
        {
            var separator = entry.IndexOf('=');
            if (separator < 0)
            {
                pairs[entry] = string.Empty;
            }
            else
            {
                pairs[entry[..separator]] = entry[(separator + 1)..];
            }
        }

        return pairs;
    }
}

/// <summary>Registro A o AAAA: la dirección IP de un host.</summary>
public sealed record AddressRecord(
    string Name,
    IPAddress Address,
    TimeSpan Ttl,
    bool CacheFlush = true)
    : DnsResourceRecord(
        Name,
        Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? DnsRecordType.Aaaa
            : DnsRecordType.A,
        Ttl,
        CacheFlush);

/// <summary>Registro cuyo tipo no manejamos: se conserva en crudo para no perder el mensaje.</summary>
public sealed record UnknownRecord(
    string Name,
    DnsRecordType Type,
    byte[] Data,
    TimeSpan Ttl,
    bool CacheFlush = false)
    : DnsResourceRecord(Name, Type, Ttl, CacheFlush);

/// <summary>Un mensaje DNS completo.</summary>
public sealed record DnsMessage(
    DnsHeader Header,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsResourceRecord> Answers,
    IReadOnlyList<DnsResourceRecord> Authorities,
    IReadOnlyList<DnsResourceRecord> Additionals)
{
    public static DnsMessage CreateQuery(params DnsQuestion[] questions) =>
        new(
            new DnsHeader(0, IsResponse: false, IsAuthoritative: false,
                (ushort)questions.Length, 0, 0, 0),
            questions,
            [],
            [],
            []);

    public static DnsMessage CreateResponse(
        IReadOnlyList<DnsResourceRecord> answers,
        IReadOnlyList<DnsResourceRecord>? additionals = null)
    {
        additionals ??= [];

        return new DnsMessage(
            new DnsHeader(
                0,
                IsResponse: true,
                IsAuthoritative: true,   // las respuestas mDNS son siempre autoritativas
                0,
                (ushort)answers.Count,
                0,
                (ushort)additionals.Count),
            [],
            answers,
            [],
            additionals);
    }
}

/// <summary>Error al codificar o decodificar un mensaje DNS.</summary>
public sealed class DnsFormatException(string message) : Exception(message);
