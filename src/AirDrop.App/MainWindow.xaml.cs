using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using WinDrop.Services;
using WinDrop.ViewModels;

namespace WinDrop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        SyncVisibilityRadios();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WebShareUrl))
        {
            UpdateQrCode(_viewModel.WebShareUrl);
        }
    }

    /// <summary>Genera el código QR de la dirección del servidor web.</summary>
    /// <remarks>
    /// Se genera localmente, sin llamar a ningún servicio externo: el requisito de que nada salga
    /// del equipo también aplica a algo tan aparentemente inocuo como un QR.
    /// </remarks>
    private void UpdateQrCode(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            QrImage.Source = null;
            return;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        QrImage.Source = bitmap;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        _viewModel.AddFiles(paths);

        // Arrastrar archivos es una intención inequívoca de enviarlos: se lleva al usuario a la
        // sección donde puede hacerlo en vez de dejarlo adivinando dónde han ido a parar.
        _viewModel.Section = AppSection.Send;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Carpeta donde guardar los archivos recibidos",
            SelectedPath = _viewModel.Settings.DownloadFolder,
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _viewModel.Settings.DownloadFolder = dialog.SelectedPath;
            _viewModel.SaveSettingsCommand.Execute(null);
        }
    }

    private void SyncVisibilityRadios()
    {
        VisibilityEveryone.IsChecked = _viewModel.Settings.Visibility == DeviceVisibility.Everyone;
        VisibilityOff.IsChecked = _viewModel.Settings.Visibility == DeviceVisibility.Off;
    }

    private void OnVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _viewModel.Settings.Visibility = VisibilityOff.IsChecked == true
            ? DeviceVisibility.Off
            : DeviceVisibility.Everyone;

        _viewModel.SaveSettingsCommand.Execute(null);
    }
}
