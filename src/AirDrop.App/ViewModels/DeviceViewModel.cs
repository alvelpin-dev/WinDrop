using AirDrop.Discovery.Mdns;

namespace WinDrop.ViewModels;

/// <summary>Un dispositivo descubierto, tal y como se muestra en la lista de destinos.</summary>
public sealed class DeviceViewModel(DiscoveredReceiver receiver) : ObservableObject
{
    private string? _resolvedName;
    private bool _isResolving;

    public DiscoveredReceiver Receiver { get; private set; } = receiver;

    public string Id => Receiver.Id;

    /// <summary>
    /// Nombre a mostrar.
    /// </summary>
    /// <remarks>
    /// Mientras no se resuelva por HTTPS solo se conoce el identificador mDNS, que es opaco por
    /// diseño. Se muestra el nombre del host como aproximación en lugar de un identificador
    /// hexadecimal sin sentido para el usuario.
    /// </remarks>
    public string DisplayName => _resolvedName
        ?? Receiver.HostName?.Replace(".local", string.Empty, StringComparison.OrdinalIgnoreCase)
        ?? Receiver.InstanceName;

    public string Details => $"{Receiver.Addresses.FirstOrDefault()}:{Receiver.Port}";

    public bool IsResolving
    {
        get => _isResolving;
        set => SetProperty(ref _isResolving, value);
    }

    /// <summary>Inicial que se muestra en el avatar circular.</summary>
    public string Initial => DisplayName.Length > 0
        ? DisplayName[0].ToString().ToUpperInvariant()
        : "?";

    public void SetResolvedName(string? name)
    {
        _resolvedName = name;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Initial));
    }

    public void Update(DiscoveredReceiver receiver)
    {
        Receiver = receiver;
        OnPropertyChanged(nameof(Details));
    }
}
