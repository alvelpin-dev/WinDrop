using System.Net;
using System.Security.Cryptography;
using AirDrop.Core.Protocol;
using AirDrop.Discovery.Dns;

namespace AirDrop.Discovery.Mdns;

/// <summary>
/// Descripción del servicio AirDrop que esta máquina anuncia por mDNS.
/// </summary>
/// <remarks>
/// <para>
/// Un detalle del protocolo que sorprende: el nombre de instancia mDNS de AirDrop <b>no</b> es el
/// nombre legible del dispositivo, sino un identificador hexadecimal opaco. El nombre que ve el
/// usuario ("PC de Álvaro") viaja después por HTTPS, en la respuesta a <c>/Discover</c>.
/// </para>
/// <para>
/// Esto es deliberado por privacidad: si el nombre real fuese al descubrimiento, cualquiera en la
/// red podría inventariar los dispositivos sin llegar a hablar con ellos. Replicamos ese
/// comportamiento en vez de anunciar el nombre en claro.
/// </para>
/// </remarks>
public sealed class AirDropServiceRegistration
{
    /// <summary>Longitud en caracteres del identificador de instancia, como en AirDrop.</summary>
    private const int InstanceIdLength = 12;

    /// <param name="instanceId">Identificador hexadecimal opaco de la instancia.</param>
    /// <param name="hostName">Nombre del host sin el sufijo <c>.local</c>.</param>
    /// <param name="addresses">Direcciones IP en las que se atiende el servicio.</param>
    /// <param name="flags">Capacidades anunciadas en el registro TXT.</param>
    /// <param name="port">Puerto TCP del servidor HTTPS.</param>
    public AirDropServiceRegistration(
        string instanceId,
        string hostName,
        IReadOnlyList<IPAddress> addresses,
        AirDropFlags flags = AirDropFlags.Supported,
        int port = MdnsConstants.AirDropPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        InstanceId = instanceId;
        HostName = hostName.EndsWith($".{MdnsConstants.LocalDomain}", StringComparison.OrdinalIgnoreCase)
            ? hostName
            : $"{hostName}.{MdnsConstants.LocalDomain}";
        Addresses = addresses;
        Flags = flags;
        Port = (ushort)port;
    }

    public string InstanceId { get; }

    /// <summary>Nombre completo de la instancia: <c>&lt;id&gt;._airdrop._tcp.local</c>.</summary>
    public string InstanceName => $"{InstanceId}.{MdnsConstants.AirDropServiceType}";

    /// <summary>Nombre del host, con el sufijo <c>.local</c>.</summary>
    public string HostName { get; }

    public IReadOnlyList<IPAddress> Addresses { get; }

    public AirDropFlags Flags { get; }

    public ushort Port { get; }

    /// <summary>Genera un identificador de instancia aleatorio con el formato de AirDrop.</summary>
    /// <remarks>
    /// Aleatorio y no derivado del equipo: un identificador estable permitiría rastrear la máquina
    /// entre redes distintas.
    /// </remarks>
    public static string GenerateInstanceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(InstanceIdLength / 2)).ToLowerInvariant();

    /// <summary>Construye los registros que responden a una consulta PTR del tipo de servicio.</summary>
    public IReadOnlyList<DnsResourceRecord> CreatePointerRecords() =>
        [new PtrRecord(MdnsConstants.AirDropServiceType, InstanceName, MdnsConstants.ServiceRecordTtl)];

    /// <summary>Construye los registros SRV y TXT de la instancia.</summary>
    public IReadOnlyList<DnsResourceRecord> CreateInstanceRecords() =>
        [
            new SrvRecord(InstanceName, HostName, Port, MdnsConstants.HostRecordTtl),
            CreateTxtRecord(),
        ];

    /// <summary>Construye los registros A y AAAA del host.</summary>
    public IReadOnlyList<DnsResourceRecord> CreateAddressRecords() =>
        [.. Addresses.Select(DnsResourceRecord (a) =>
            new AddressRecord(HostName, a, MdnsConstants.HostRecordTtl))];

    /// <summary>Construye el conjunto completo de registros del anuncio.</summary>
    public IReadOnlyList<DnsResourceRecord> CreateAllRecords() =>
        [.. CreatePointerRecords(), .. CreateInstanceRecords(), .. CreateAddressRecords()];

    /// <summary>
    /// Construye los registros de despedida: los mismos, con TTL cero.
    /// </summary>
    /// <remarks>
    /// Se envían al cerrar la aplicación para que los demás nos retiren de sus cachés al momento
    /// en vez de seguir viéndonos durante lo que dure el TTL.
    /// </remarks>
    public IReadOnlyList<DnsResourceRecord> CreateGoodbyeRecords() =>
        [.. CreateAllRecords().Select(WithZeroTtl)];

    private DnsResourceRecord CreateTxtRecord() =>
        TxtRecord.FromPairs(
            InstanceName,
            new Dictionary<string, string>
            {
                ["flags"] = ((int)Flags).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            MdnsConstants.ServiceRecordTtl);

    private static DnsResourceRecord WithZeroTtl(DnsResourceRecord record) => record switch
    {
        PtrRecord ptr => ptr with { Ttl = MdnsConstants.GoodbyeTtl },
        SrvRecord srv => srv with { Ttl = MdnsConstants.GoodbyeTtl },
        TxtRecord txt => txt with { Ttl = MdnsConstants.GoodbyeTtl },
        AddressRecord address => address with { Ttl = MdnsConstants.GoodbyeTtl },
        _ => record,
    };
}
