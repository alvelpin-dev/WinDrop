using System.Windows;
using WinDrop.Services;

namespace WinDrop;

/// <summary>
/// Diálogo de consentimiento para una transferencia entrante.
/// </summary>
/// <remarks>
/// Mientras está abierto, el emisor mantiene su petición <c>/Ask</c> en espera. Si el emisor
/// cancela, el token se dispara y el diálogo se cierra solo, para no dejar en pantalla una
/// pregunta cuya respuesta ya no le importa a nadie.
/// </remarks>
public partial class AcceptDialog : Window
{
    private readonly TaskCompletionSource<bool> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AcceptDialog(AcceptancePrompt prompt)
    {
        InitializeComponent();

        HeadlineText.Text = prompt.FileNames.Count == 1
            ? $"{prompt.SenderName} quiere enviarte un archivo"
            : $"{prompt.SenderName} quiere enviarte {prompt.FileNames.Count} archivos";

        SubtitleText.Text = "¿Aceptar la transferencia?";
        FileList.ItemsSource = prompt.FileNames;
    }

    /// <summary>Muestra el diálogo y espera la decisión del usuario.</summary>
    public async Task<bool> ShowAndWaitAsync(CancellationToken cancellationToken)
    {
        // Cerrar el diálogo si el emisor se cansa de esperar.
        await using var registration = cancellationToken.Register(() =>
            Dispatcher.Invoke(() =>
            {
                _result.TrySetResult(false);
                Close();
            }));

        Show();
        Activate();

        return await _result.Task;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Cerrar con la X equivale a rechazar: nunca se acepta por omisión.
        _result.TrySetResult(false);
        base.OnClosed(e);
    }
}
