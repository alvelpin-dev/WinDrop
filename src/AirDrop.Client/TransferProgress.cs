namespace AirDrop.Client;

/// <summary>Fases por las que pasa un envío.</summary>
public enum TransferPhase
{
    /// <summary>Resolviendo la identidad del destino con <c>/Discover</c>.</summary>
    Discovering,

    /// <summary>Esperando a que el usuario del otro dispositivo acepte.</summary>
    WaitingForAcceptance,

    /// <summary>Enviando los datos.</summary>
    Uploading,

    Completed,
    Rejected,
    Failed,
    Cancelled,
}

/// <summary>Estado de un envío en curso.</summary>
/// <param name="Phase">Fase actual.</param>
/// <param name="BytesSent">Bytes enviados hasta el momento.</param>
/// <param name="TotalBytes">Total a enviar, o cero si aún no se conoce.</param>
/// <param name="CurrentFileName">Fichero que se está enviando.</param>
public readonly record struct TransferProgress(
    TransferPhase Phase,
    long BytesSent = 0,
    long TotalBytes = 0,
    string? CurrentFileName = null)
{
    /// <summary>Fracción completada entre 0 y 1, o <c>null</c> si el total es desconocido.</summary>
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)BytesSent / TotalBytes, 0, 1)
        : null;
}

/// <summary>Resultado de un envío.</summary>
/// <param name="Phase">Fase final alcanzada.</param>
/// <param name="ReceiverName">Nombre legible del receptor, si llegó a conocerse.</param>
/// <param name="Error">Causa del fallo, si lo hubo.</param>
public sealed record TransferResult(
    TransferPhase Phase,
    string? ReceiverName = null,
    Exception? Error = null)
{
    public bool Succeeded => Phase == TransferPhase.Completed;
}
