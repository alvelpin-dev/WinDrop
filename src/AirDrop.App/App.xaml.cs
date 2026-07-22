using System.Windows;
using System.Windows.Threading;
using AirDrop.Server.Web;
using Microsoft.Extensions.Logging;
using WinDrop.Services;
using WinDrop.ViewModels;

namespace WinDrop;

/// <summary>
/// Punto de entrada y composición de la aplicación.
/// </summary>
/// <remarks>
/// Las dependencias se cablean a mano en lugar de usar un contenedor: el grafo es pequeño y
/// explícito, y así se ve de un vistazo qué depende de qué sin indirecciones.
/// </remarks>
public partial class App : Application
{
    private FileLoggerProvider? _loggerProvider;
    private ILoggerFactory? _loggerFactory;
    private NotificationService? _notifications;
    private MainViewModel? _viewModel;
    private AppSettings? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Un fallo no controlado no debe cerrar la aplicación en silencio: se registra y se
        // informa, porque un cierre inexplicable a mitad de una transferencia es lo peor
        // que le puede pasar al usuario.
        DispatcherUnhandledException += OnUnhandledException;

        _loggerProvider = new FileLoggerProvider();
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(_loggerProvider);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var logger = _loggerFactory.CreateLogger<App>();
        logger.LogInformation("WinDrop iniciado");

        var settingsStore = new SettingsStore();
        _settings = settingsStore.Load();

        _notifications = new NotificationService();

        var receiver = new FileReceiver(
            () => _settings,
            AskUserAsync,
            _loggerFactory.CreateLogger<FileReceiver>());

        var airDropService = new AirDropService(() => _settings, receiver, _loggerFactory);

        var webShare = new WebShareServer(_loggerFactory.CreateLogger<WebShareServer>());

        _viewModel = new MainViewModel(
            airDropService,
            receiver,
            settingsStore,
            _settings,
            new TransferHistory(),
            webShare,
            _loggerFactory);

        receiver.TransferCompleted += (files, sender) => Dispatcher.Invoke(() =>
        {
            if (_settings.ShowNotifications)
            {
                _notifications.NotifyReceived(
                    [.. files.Select(f => f.FileName)],
                    sender,
                    _settings.DownloadFolder,
                    _settings.PlaySound);
            }
        });

        receiver.TransferFailed += error => Dispatcher.Invoke(() =>
        {
            if (_settings.ShowNotifications)
            {
                _notifications.NotifyError(error.Message);
            }
        });

        var window = new MainWindow(_viewModel);
        _notifications.ShowMainWindowRequested += () => Dispatcher.Invoke(() =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });

        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Eleva la petición de consentimiento a la interfaz.
    /// </summary>
    /// <remarks>
    /// La llama el servidor desde un hilo del pool mientras atiende <c>/Ask</c>, así que hay que
    /// saltar al hilo de interfaz para poder abrir la ventana.
    /// </remarks>
    private async Task<bool> AskUserAsync(AcceptancePrompt prompt, CancellationToken cancellationToken)
    {
        var dialog = await Dispatcher.InvokeAsync(() => new AcceptDialog(prompt));
        return await dialog.ShowAndWaitAsync(cancellationToken);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggerFactory?.CreateLogger<App>()
            .LogCritical(e.Exception, "Excepción no controlada en la interfaz");

        MessageBox.Show(
            $"Se ha producido un error inesperado:\n\n{e.Exception.Message}\n\n" +
            $"Los detalles están en:\n{_loggerProvider?.LogPath}",
            "WinDrop",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Se marca como gestionada para que la aplicación siga viva: un fallo aislado en una
        // pantalla no justifica perder una transferencia en curso.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggerFactory?.CreateLogger<App>().LogInformation("WinDrop cerrándose");

        // Se espera al cierre ordenado para que salgan las despedidas mDNS: sin ellas, los demás
        // dispositivos nos seguirían viendo durante lo que dure el TTL.
        _viewModel?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));

        _notifications?.Dispose();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
