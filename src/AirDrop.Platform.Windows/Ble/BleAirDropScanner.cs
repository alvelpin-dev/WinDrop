using AirDrop.Core.Protocol.Ble;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AirDrop.Platform.Windows.Ble;

/// <summary>Un dispositivo Apple detectado emitiendo anuncios de Continuity.</summary>
/// <param name="Address">Dirección Bluetooth, aleatorizada por privacidad.</param>
/// <param name="SignalStrength">RSSI en dBm. Más cerca de cero significa más cerca.</param>
/// <param name="MessageTypes">Tipos de mensaje Continuity presentes en el anuncio.</param>
/// <param name="AirDrop">Mensaje de AirDrop, si el anuncio lo incluye.</param>
/// <param name="Seen">Momento de la detección.</param>
public sealed record ContinuityDetection(
    ulong Address,
    short SignalStrength,
    IReadOnlyList<ContinuityMessageType> MessageTypes,
    AirDropAdvertisement? AirDrop,
    DateTimeOffset Seen)
{
    /// <summary>Indica si el dispositivo está anunciando AirDrop en este momento.</summary>
    public bool IsAdvertisingAirDrop => AirDrop is not null;

    /// <summary>Estimación grosera de distancia a partir del RSSI.</summary>
    public string Proximity => SignalStrength switch
    {
        > -50 => "muy cerca",
        > -70 => "cerca",
        > -85 => "en la sala",
        _ => "lejos",
    };
}

/// <summary>
/// Escucha los anuncios BLE de Continuity de los dispositivos Apple cercanos.
/// </summary>
/// <remarks>
/// <para>
/// Detecta el momento exacto en que alguien abre la hoja de compartir de AirDrop en un iPhone
/// cercano, porque es entonces cuando el teléfono empieza a emitir el mensaje de tipo 0x05.
/// </para>
/// <para>
/// Windows sí puede recibir estos anuncios. Lo que no puede es completar lo que viene después:
/// el iPhone espera encontrar al receptor por AWDL, y ahí es donde se corta la cadena.
/// </para>
/// </remarks>
public sealed class BleAirDropScanner(ILogger<BleAirDropScanner>? logger = null) : IDisposable
{
    private readonly ILogger<BleAirDropScanner> _logger =
        logger ?? NullLogger<BleAirDropScanner>.Instance;

    private BluetoothLEAdvertisementWatcher? _watcher;

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>Se dispara por cada anuncio de un dispositivo Apple.</summary>
    public event Action<ContinuityDetection>? AppleDeviceDetected;

    /// <summary>Empieza a escuchar.</summary>
    public bool Start()
    {
        if (_watcher is not null)
        {
            return IsScanning;
        }

        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                // El modo activo pide además la respuesta de escaneo, que es donde algunos
                // dispositivos reparten el resto de los datos.
                ScanningMode = BluetoothLEScanningMode.Active,
            };

            // Filtrar por el identificador de Apple en el propio watcher reduce muchísimo el
            // trabajo: en un entorno con vecinos, los anuncios BLE llegan por decenas por segundo.
            _watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                new BluetoothLEManufacturerData
                {
                    CompanyId = AirDropAdvertisement.AppleCompanyId,
                });

            _watcher.Received += OnAdvertisementReceived;
            _watcher.Start();

            _logger.LogInformation("Escaneo BLE de dispositivos Apple iniciado");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo iniciar el escaneo BLE");
            _watcher = null;
            return false;
        }
    }

    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallo deteniendo el escaneo BLE");
        }
        finally
        {
            _watcher = null;
            _logger.LogInformation("Escaneo BLE detenido");
        }
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != AirDropAdvertisement.AppleCompanyId)
            {
                continue;
            }

            var data = ReadBuffer(section.Data);
            var types = AirDropAdvertisement.ListMessageTypes(data);
            AirDropAdvertisement.TryParse(data, out var airDrop);

            if (airDrop is not null)
            {
                _logger.LogInformation(
                    "Dispositivo Apple anunciando AirDrop detectado: {Address:X} a {Rssi} dBm",
                    args.BluetoothAddress,
                    args.RawSignalStrengthInDBm);
            }

            AppleDeviceDetected?.Invoke(new ContinuityDetection(
                args.BluetoothAddress,
                args.RawSignalStrengthInDBm,
                types,
                airDrop,
                args.Timestamp));
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var data = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(data);
        return data;
    }

    public void Dispose() => Stop();
}
