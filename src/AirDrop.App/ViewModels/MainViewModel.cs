using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AirDrop.Client;
using AirDrop.Discovery.Mdns;
using AirDrop.Server;
using AirDrop.Server.Web;
using Microsoft.Extensions.Logging;
using WinDrop.Services;

namespace WinDrop.ViewModels;

/// <summary>Secciones de la ventana principal.</summary>
public enum AppSection
{
    Receive,
    Send,
    WebShare,
    History,
    Settings,
}

/// <summary>Estado de la ventana principal.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AirDropService _airDrop;
    private readonly FileReceiver _receiver;
    private readonly SettingsStore _settingsStore;
    private readonly WebShareServer _webShare;
    private readonly ILogger<MainViewModel> _logger;

    private AppSection _section = AppSection.Receive;
    private bool _isReceiving;
    private string _statusMessage = "Recepción desactivada";
    private bool _isBusy;
    private CancellationTokenSource? _sendCancellation;

    private double? _progressFraction;
    private string? _progressText;
    private bool _isTransferring;

    private bool _isWebShareActive;
    private string? _webShareUrl;

    private string? _bluetoothStatus;
    private string? _nearbyAppleStatus;

    /// <summary>Última vez que se vio un iPhone anunciando AirDrop por Bluetooth.</summary>
    private DateTimeOffset _lastAirDropSighting = DateTimeOffset.MinValue;

    public MainViewModel(
        AirDropService airDrop,
        FileReceiver receiver,
        SettingsStore settingsStore,
        AppSettings settings,
        TransferHistory history,
        WebShareServer webShare,
        ILoggerFactory loggerFactory)
    {
        _airDrop = airDrop;
        _receiver = receiver;
        _settingsStore = settingsStore;
        _webShare = webShare;
        _logger = loggerFactory.CreateLogger<MainViewModel>();

        Settings = settings;
        History = history;

        _airDrop.DeviceDiscovered += OnDeviceDiscovered;
        _airDrop.DeviceLost += OnDeviceLost;
        _airDrop.AppleDeviceNearby += OnAppleDeviceNearby;
        _airDrop.BluetoothStateChanged += OnBluetoothStateChanged;
        _receiver.TransferCompleted += OnTransferCompleted;
        _receiver.TransferFailed += OnTransferFailed;
        _webShare.FileUploaded += OnWebShareUpload;

        ToggleReceivingCommand = new AsyncRelayCommand(ToggleReceivingAsync);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        CancelSendCommand = new RelayCommand(CancelSend, () => _isTransferring);
        ClearFilesCommand = new RelayCommand(ClearFiles);
        ToggleWebShareCommand = new AsyncRelayCommand(ToggleWebShareAsync);
        ClearHistoryCommand = new RelayCommand(() => History.Clear());
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is AppSection target)
            {
                Section = target;
            }
        });
    }

    public AppSettings Settings { get; }

    public TransferHistory History { get; }

    /// <summary>Dispositivos encontrados en la red.</summary>
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    /// <summary>Ficheros que el usuario ha seleccionado para enviar.</summary>
    public ObservableCollection<FileToSend> SelectedFiles { get; } = [];

    private DeviceViewModel? _selectedDevice;

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AppSection Section
    {
        get => _section;
        set
        {
            if (SetProperty(ref _section, value))
            {
                OnPropertyChanged(nameof(IsReceiveSection));
                OnPropertyChanged(nameof(IsSendSection));
                OnPropertyChanged(nameof(IsWebShareSection));
                OnPropertyChanged(nameof(IsHistorySection));
                OnPropertyChanged(nameof(IsSettingsSection));

                // Buscar dispositivos solo mientras la sección de envío está a la vista evita
                // tráfico multicast innecesario el resto del tiempo.
                if (value == AppSection.Send)
                {
                    _ = RefreshDevicesAsync();
                }
            }
        }
    }

    public bool IsReceiveSection => Section == AppSection.Receive;

    public bool IsSendSection => Section == AppSection.Send;

    public bool IsWebShareSection => Section == AppSection.WebShare;

    public bool IsHistorySection => Section == AppSection.History;

    public bool IsSettingsSection => Section == AppSection.Settings;

    public bool IsReceiving
    {
        get => _isReceiving;
        private set
        {
            if (SetProperty(ref _isReceiving, value))
            {
                OnPropertyChanged(nameof(ReceivingActionText));
            }
        }
    }

    public string ReceivingActionText => IsReceiving ? "Desactivar" : "Activar";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsTransferring
    {
        get => _isTransferring;
        private set
        {
            if (SetProperty(ref _isTransferring, value))
            {
                CancelSendCommand.RaiseCanExecuteChanged();
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double? ProgressFraction
    {
        get => _progressFraction;
        private set
        {
            if (SetProperty(ref _progressFraction, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
            }
        }
    }

    public double ProgressPercent => (ProgressFraction ?? 0) * 100;

    public bool IsProgressIndeterminate => ProgressFraction is null;

    public string? ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool CanSend => SelectedDevice is not null && SelectedFiles.Count > 0 && !IsTransferring;

    public bool IsWebShareActive
    {
        get => _isWebShareActive;
        private set
        {
            if (SetProperty(ref _isWebShareActive, value))
            {
                OnPropertyChanged(nameof(WebShareActionText));
            }
        }
    }

    public string WebShareActionText => IsWebShareActive ? "Detener" : "Activar";

    public string? WebShareUrl
    {
        get => _webShareUrl;
        private set => SetProperty(ref _webShareUrl, value);
    }

    /// <summary>Estado del anuncio Bluetooth, para mostrarlo en la interfaz.</summary>
    public string? BluetoothStatus
    {
        get => _bluetoothStatus;
        private set
        {
            if (SetProperty(ref _bluetoothStatus, value))
            {
                OnPropertyChanged(nameof(HasBluetoothStatus));
            }
        }
    }

    public bool HasBluetoothStatus => !string.IsNullOrEmpty(BluetoothStatus);

    /// <summary>Aviso de que hay un iPhone cerca intentando usar AirDrop.</summary>
    /// <remarks>
    /// Es lo más útil que puede hacer la capa Bluetooth aquí: no permite aparecer en la pantalla
    /// del iPhone, pero sí saber que alguien lo está intentando y ofrecer la alternativa.
    /// </remarks>
    public string? NearbyAppleStatus
    {
        get => _nearbyAppleStatus;
        private set
        {
            if (SetProperty(ref _nearbyAppleStatus, value))
            {
                OnPropertyChanged(nameof(HasNearbyApple));
            }
        }
    }

    public bool HasNearbyApple => !string.IsNullOrEmpty(NearbyAppleStatus);

    public AsyncRelayCommand ToggleReceivingCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand CancelSendCommand { get; }

    public RelayCommand ClearFilesCommand { get; }

    public AsyncRelayCommand ToggleWebShareCommand { get; }

    public RelayCommand ClearHistoryCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand NavigateCommand { get; }

    /// <summary>Arranque inicial de la aplicación.</summary>
    public async Task InitializeAsync()
    {
        History.Load();

        if (Settings.StartReceivingOnLaunch && Settings.Visibility != DeviceVisibility.Off)
        {
            await StartReceivingAsync();
        }

        await _airDrop.StartDiscoveryAsync();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Una carpeta arrastrada se expande: enviar el directorio como tal exigiría
                // reproducir la jerarquía en el CPIO, que aún no está implementado.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    AddFile(file);
                }
            }
            else if (File.Exists(path))
            {
                AddFile(path);
            }
        }

        UpdateWebShareFiles();
        SendCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend));
    }

    private void AddFile(string path)
    {
        if (SelectedFiles.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SelectedFiles.Add(FileToSend.FromPath(path));
    }

    private void ClearFiles()
    {
        SelectedFiles.Clear();
        UpdateWebShareFiles();
        SendCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend));
    }

    private async Task ToggleReceivingAsync()
    {
        if (IsReceiving)
        {
            await _airDrop.StopReceivingAsync();
            IsReceiving = false;
            StatusMessage = "Recepción desactivada";
        }
        else
        {
            await StartReceivingAsync();
        }
    }

    private async Task StartReceivingAsync()
    {
        IsBusy = true;

        try
        {
            await _airDrop.StartReceivingAsync();
            IsReceiving = true;
            StatusMessage = $"Visible como «{Settings.DeviceName}»";
        }
        catch (InvalidOperationException ex)
        {
            // El caso frecuente es el puerto ocupado, y el mensaje ya explica qué hacer.
            _logger.LogError(ex, "No se pudo activar la recepción");
            StatusMessage = ex.Message;
            IsReceiving = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        IsBusy = true;

        try
        {
            await _airDrop.StartDiscoveryAsync();
            await _airDrop.RefreshDevicesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDeviceDiscovered(DiscoveredReceiver receiver)
    {
        // Los eventos llegan desde el hilo de red; la colección la observa la interfaz.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.Id == receiver.Id);

            if (existing is not null)
            {
                existing.Update(receiver);
                return;
            }

            var device = new DeviceViewModel(receiver);
            Devices.Add(device);
            _ = ResolveNameAsync(device);
        });
    }

    private async Task ResolveNameAsync(DeviceViewModel device)
    {
        device.IsResolving = true;

        try
        {
            var name = await _airDrop.ResolveDeviceNameAsync(device.Receiver);
            Application.Current?.Dispatcher.Invoke(() => device.SetResolvedName(name));
        }
        finally
        {
            Application.Current?.Dispatcher.Invoke(() => device.IsResolving = false);
        }
    }

    private void OnBluetoothStateChanged(AirDrop.Platform.Windows.Ble.AdvertiserState state) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            BluetoothStatus = state switch
            {
                AirDrop.Platform.Windows.Ble.AdvertiserState.Started =>
                    "Bluetooth: emitiendo el anuncio de AirDrop",
                AirDrop.Platform.Windows.Ble.AdvertiserState.Aborted =>
                    "Bluetooth: el sistema rechazó el anuncio",
                AirDrop.Platform.Windows.Ble.AdvertiserState.Unavailable =>
                    "Bluetooth: no disponible",
                _ => null,
            };
        });

    private void OnAppleDeviceNearby(AirDrop.Platform.Windows.Ble.ContinuityDetection detection)
    {
        if (!detection.IsAdvertisingAirDrop)
        {
            return;   // el resto de anuncios de Continuity son constantes y no significan nada aquí
        }

        // Se limita la frecuencia: un iPhone con la hoja abierta emite varias veces por segundo.
        var now = DateTimeOffset.Now;
        if (now - _lastAirDropSighting < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastAirDropSighting = now;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            NearbyAppleStatus =
                $"Hay un dispositivo Apple {detection.Proximity} intentando usar AirDrop " +
                $"({detection.SignalStrength} dBm). No puede vernos: usa la sección iPhone.";

            _logger.LogInformation(
                "iPhone cercano anunciando AirDrop: {Address:X12} a {Rssi} dBm",
                detection.Address,
                detection.SignalStrength);
        });
    }

    private void OnDeviceLost(string id) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device is not null)
            {
                Devices.Remove(device);
            }
        });

    private async Task SendAsync()
    {
        if (SelectedDevice is null || SelectedFiles.Count == 0)
        {
            return;
        }

        var device = SelectedDevice;
        var files = SelectedFiles.ToList();

        _sendCancellation = new CancellationTokenSource();
        IsTransferring = true;

        var progress = new Progress<TransferProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressText = p.Phase switch
            {
                TransferPhase.Discovering => "Conectando…",
                TransferPhase.WaitingForAcceptance => "Esperando a que acepten…",
                TransferPhase.Uploading => p.CurrentFileName is null
                    ? "Enviando…"
                    : $"Enviando {p.CurrentFileName}…",
                TransferPhase.Completed => "Completado",
                _ => null,
            };
        });

        try
        {
            var result = await _airDrop.SendAsync(
                device.Receiver, files, progress, _sendCancellation.Token);

            History.Add(new TransferRecord
            {
                Timestamp = DateTimeOffset.Now,
                Direction = TransferDirection.Sent,
                Status = result.Phase switch
                {
                    TransferPhase.Completed => TransferStatus.Completed,
                    TransferPhase.Rejected => TransferStatus.Rejected,
                    TransferPhase.Cancelled => TransferStatus.Cancelled,
                    _ => TransferStatus.Failed,
                },
                PeerName = result.ReceiverName ?? device.DisplayName,
                FileNames = [.. files.Select(f => f.Name)],
                TotalBytes = files.Sum(f => f.Length),
                ErrorMessage = result.Error?.Message,
            });

            StatusMessage = result.Phase switch
            {
                TransferPhase.Completed => $"Enviado a {result.ReceiverName ?? device.DisplayName}",
                TransferPhase.Rejected => "El destinatario rechazó la transferencia",
                TransferPhase.Cancelled => "Envío cancelado",
                _ => $"No se pudo enviar: {result.Error?.Message}",
            };

            if (result.Succeeded)
            {
                ClearFiles();
            }
        }
        finally
        {
            IsTransferring = false;
            ProgressFraction = null;
            ProgressText = null;
            _sendCancellation?.Dispose();
            _sendCancellation = null;
        }
    }

    private void CancelSend() => _sendCancellation?.Cancel();

    private async Task ToggleWebShareAsync()
    {
        if (IsWebShareActive)
        {
            await _webShare.DisposeAsync();
            IsWebShareActive = false;
            WebShareUrl = null;
            return;
        }

        Directory.CreateDirectory(Settings.DownloadFolder);

        await _webShare.StartAsync(name =>
        {
            var destination = Path.Combine(Settings.DownloadFolder, Path.GetFileName(name));
            return File.Create(MakeUnique(destination));
        });

        UpdateWebShareFiles();

        WebShareUrl = _webShare.GetAccessUrls().FirstOrDefault();
        IsWebShareActive = WebShareUrl is not null;

        if (WebShareUrl is null)
        {
            StatusMessage = "No se encontró ninguna dirección de red local utilizable";
        }
    }

    private void UpdateWebShareFiles()
    {
        if (IsWebShareActive || _webShare.Port > 0)
        {
            _webShare.SetSharedFiles(SelectedFiles.Select(f => f.Path));
        }
    }

    private void OnWebShareUpload(string name, long bytes) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            History.Add(new TransferRecord
            {
                Timestamp = DateTimeOffset.Now,
                Direction = TransferDirection.Received,
                Status = TransferStatus.Completed,
                PeerName = "Navegador",
                FileNames = [name],
                TotalBytes = bytes,
                Folder = Settings.DownloadFolder,
            });

            StatusMessage = $"Recibido {name}";
        });

    private void OnTransferCompleted(IReadOnlyList<ReceivedFile> files, string sender) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            History.Add(new TransferRecord
            {
                Timestamp = DateTimeOffset.Now,
                Direction = TransferDirection.Received,
                Status = TransferStatus.Completed,
                PeerName = sender,
                FileNames = [.. files.Select(f => f.FileName)],
                TotalBytes = files.Sum(f => f.Length),
                Folder = Settings.DownloadFolder,
            });

            StatusMessage = files.Count == 1
                ? $"Recibido {files[0].FileName} de {sender}"
                : $"Recibidos {files.Count} archivos de {sender}";
        });

    private void OnTransferFailed(Exception error) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            History.Add(new TransferRecord
            {
                Timestamp = DateTimeOffset.Now,
                Direction = TransferDirection.Received,
                Status = TransferStatus.Failed,
                PeerName = "Desconocido",
                FileNames = [],
                ErrorMessage = error.Message,
            });

            StatusMessage = $"Transferencia fallida: {error.Message}";
        });

    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(Settings.DownloadFolder);
        Process.Start(new ProcessStartInfo(Settings.DownloadFolder) { UseShellExecute = true });
    }

    private void SaveSettings()
    {
        _settingsStore.Save(Settings);
        StatusMessage = "Preferencias guardadas";

        if (IsReceiving)
        {
            StatusMessage = "Preferencias guardadas. Reactiva la recepción para aplicar el nombre.";
        }
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({DateTime.Now:yyyyMMddHHmmss}){extension}");
    }

    public async ValueTask DisposeAsync()
    {
        await _webShare.DisposeAsync();
        await _airDrop.DisposeAsync();
    }
}
