using System.Net;

namespace AirDrop.Discovery.Mdns;

/// <summary>Constantes del protocolo mDNS y del servicio AirDrop.</summary>
public static class MdnsConstants
{
    /// <summary>Puerto de multicast DNS.</summary>
    public const int Port = 5353;

    /// <summary>Grupo multicast IPv4 de mDNS.</summary>
    public static readonly IPAddress MulticastAddressV4 = IPAddress.Parse("224.0.0.251");

    /// <summary>Grupo multicast IPv6 de mDNS, de alcance link-local.</summary>
    public static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::fb");

    public static readonly IPEndPoint MulticastEndPointV4 = new(MulticastAddressV4, Port);
    public static readonly IPEndPoint MulticastEndPointV6 = new(MulticastAddressV6, Port);

    /// <summary>Tipo de servicio DNS-SD de AirDrop.</summary>
    public const string AirDropServiceType = "_airdrop._tcp.local";

    /// <summary>Puerto TCP del servidor HTTPS de AirDrop.</summary>
    public const int AirDropPort = 8770;

    /// <summary>
    /// TTL de los registros ligados al host (A, AAAA, SRV).
    /// </summary>
    /// <remarks>
    /// Corto a propósito: son los que cambian cuando el equipo cambia de IP, y un TTL largo
    /// dejaría a los demás intentando conectar a una dirección muerta.
    /// </remarks>
    public static readonly TimeSpan HostRecordTtl = TimeSpan.FromSeconds(120);

    /// <summary>TTL de los registros que no dependen de la dirección (PTR, TXT).</summary>
    public static readonly TimeSpan ServiceRecordTtl = TimeSpan.FromSeconds(4500);

    /// <summary>TTL cero: retira un registro de las cachés ajenas.</summary>
    public static readonly TimeSpan GoodbyeTtl = TimeSpan.Zero;

    /// <summary>Sufijo del dominio local de mDNS.</summary>
    public const string LocalDomain = "local";
}
