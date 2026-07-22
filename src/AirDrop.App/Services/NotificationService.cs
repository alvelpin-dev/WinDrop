using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;

namespace WinDrop.Services;

/// <summary>
/// Notificaciones de escritorio y icono en la bandeja del sistema.
/// </summary>
/// <remarks>
/// Usa <see cref="NotifyIcon"/> en lugar de las notificaciones toast de Windows 10+. Los toast
/// exigen una identidad de aplicación registrada (AppUserModelID), que una aplicación portable sin
/// instalador no tiene; intentarlo produce notificaciones que simplemente no aparecen. La bandeja
/// funciona siempre y no requiere instalación.
/// </remarks>
public sealed class NotificationService : IDisposable
{
    private readonly NotifyIcon _icon;

    /// <summary>Carpeta que se abrirá si el usuario pulsa la notificación.</summary>
    private string? _folderToOpen;

    public NotificationService()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WinDrop",
            Visible = true,
        };

        _icon.BalloonTipClicked += (_, _) => OpenFolder();
        _icon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
    }

    /// <summary>Se dispara al hacer doble clic en el icono de la bandeja.</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>Muestra una notificación de archivos recibidos.</summary>
    public void NotifyReceived(
        IReadOnlyList<string> fileNames,
        string senderName,
        string folder,
        bool playSound)
    {
        _folderToOpen = folder;

        var title = fileNames.Count == 1
            ? "Archivo recibido"
            : $"{fileNames.Count} archivos recibidos";

        var body = fileNames.Count == 1
            ? $"{fileNames[0]} — de {senderName}"
            : $"De {senderName}";

        _icon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);

        if (playSound)
        {
            SystemSounds.Asterisk.Play();
        }
    }

    /// <summary>Muestra una notificación de error.</summary>
    public void NotifyError(string message)
    {
        _folderToOpen = null;
        _icon.ShowBalloonTip(5000, "Transferencia fallida", message, ToolTipIcon.Error);
    }

    private void OpenFolder()
    {
        if (string.IsNullOrEmpty(_folderToOpen) || !Directory.Exists(_folderToOpen))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_folderToOpen) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
