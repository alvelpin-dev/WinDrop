using System.Net;
using AirDrop.Core.Protocol.Messages;

namespace AirDrop.Server;

/// <summary>Identidad que este equipo presenta a los emisores.</summary>
/// <param name="ComputerName">Nombre visible en la hoja de AirDrop del emisor.</param>
/// <param name="ModelName">Identificador de modelo.</param>
public sealed record ReceiverIdentity(string ComputerName, string ModelName = "Windows11,1");

/// <summary>Petición entrante que espera la decisión del usuario.</summary>
/// <param name="Request">Contenido del <c>/Ask</c>.</param>
/// <param name="SenderAddress">Dirección desde la que llega.</param>
/// <param name="SenderIsVerified">
/// Si la firma del <c>SenderRecordData</c> se validó contra la CA de Apple. Es información para
/// mostrar al usuario, nunca una puerta de acceso.
/// </param>
public sealed record IncomingTransferRequest(
    AskRequest Request,
    IPAddress SenderAddress,
    bool SenderIsVerified = false);

/// <summary>Un fichero ya escrito en disco.</summary>
public sealed record ReceivedFile(string FileName, string Path, long Length);

/// <summary>
/// Punto de extensión donde la aplicación decide qué hacer con las transferencias entrantes.
/// </summary>
/// <remarks>
/// El servidor no sabe nada de interfaz de usuario, de carpetas de destino ni de notificaciones.
/// Solo habla el protocolo y delega aquí las decisiones. Esto es lo que permite probar el servidor
/// entero sin UI, y lo que dejaría cambiar la UI sin tocar el protocolo.
/// </remarks>
public interface IIncomingTransferHandler
{
    /// <summary>Identidad que se devuelve en <c>/Discover</c> y <c>/Ask</c>.</summary>
    ReceiverIdentity Identity { get; }

    /// <summary>
    /// Decide si se acepta una transferencia.
    /// </summary>
    /// <remarks>
    /// Puede tardar minutos: el emisor mantiene la petición abierta mientras el usuario decide.
    /// La implementación debe respetar el <paramref name="cancellationToken"/>, que se dispara si
    /// el emisor cancela.
    /// </remarks>
    /// <returns><c>true</c> para aceptar, <c>false</c> para rechazar.</returns>
    Task<bool> ShouldAcceptAsync(
        IncomingTransferRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Abre el destino donde escribir un fichero entrante.</summary>
    /// <param name="fileName">Nombre tal y como viene en el archivo, sin sanear.</param>
    Task<Stream> OpenFileForWritingAsync(
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>Notifica que la transferencia terminó correctamente.</summary>
    Task OnTransferCompletedAsync(
        IReadOnlyList<ReceivedFile> files,
        CancellationToken cancellationToken = default);

    /// <summary>Notifica que la transferencia falló, para poder limpiar los ficheros parciales.</summary>
    Task OnTransferFailedAsync(Exception error, CancellationToken cancellationToken = default);
}
