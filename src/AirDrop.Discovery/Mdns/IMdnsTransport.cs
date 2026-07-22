using System.Net;

namespace AirDrop.Discovery.Mdns;

/// <summary>Un paquete mDNS recibido.</summary>
/// <param name="Payload">Bytes crudos del datagrama.</param>
/// <param name="Source">Remitente, necesario para poder contestar por unicast.</param>
public readonly record struct MdnsPacket(byte[] Payload, IPEndPoint Source);

/// <summary>
/// Transporte de datagramas mDNS.
/// </summary>
/// <remarks>
/// Existe para separar la lógica del protocolo de los sockets. El responder y el browser son la
/// parte del descubrimiento con más casos límite, y con esta interfaz se pueden probar por completo
/// sin abrir un socket, sin depender de la red del equipo y sin resultados intermitentes.
/// </remarks>
public interface IMdnsTransport : IAsyncDisposable
{
    /// <summary>Se dispara por cada datagrama recibido.</summary>
    event Action<MdnsPacket>? PacketReceived;

    /// <summary>Empieza a escuchar.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía un datagrama.
    /// </summary>
    /// <param name="payload">Mensaje DNS ya codificado.</param>
    /// <param name="destination">
    /// Destino unicast, o <c>null</c> para enviar a los grupos multicast de mDNS.
    /// </param>
    Task SendAsync(
        byte[] payload,
        IPEndPoint? destination = null,
        CancellationToken cancellationToken = default);
}
