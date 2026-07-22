using AirDrop.Core.Protocol.Ble;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AirDrop.Platform.Windows.Ble;

/// <summary>Estado del emisor de anuncios BLE.</summary>
public enum AdvertiserState
{
    Stopped,
    Started,

    /// <summary>Windows aceptó la petición pero el adaptador no la puso en marcha.</summary>
    Aborted,

    /// <summary>No hay adaptador Bluetooth utilizable.</summary>
    Unavailable,
}

/// <summary>
/// Emite el anuncio BLE de AirDrop desde Windows.
/// </summary>
/// <remarks>
/// <para>
/// Es la capa que en AirDrop hace de <b>timbre</b>: avisa a los dispositivos cercanos de que hay
/// una transferencia en marcha para que enciendan su AWDL. No transporta datos ni sustituye al
/// descubrimiento; ver <see cref="AirDropAdvertisement"/>.
/// </para>
/// <para>
/// Windows permite publicar datos de fabricante arbitrarios, incluido el identificador de Apple,
/// así que el anuncio se emite tal cual lo haría un dispositivo Apple. Lo que Windows <b>no</b>
/// puede hacer es responder después por AWDL, que es lo que el iPhone busca a continuación.
/// </para>
/// </remarks>
public sealed class BleAirDropAdvertiser(ILogger<BleAirDropAdvertiser>? logger = null) : IDisposable
{
    private readonly ILogger<BleAirDropAdvertiser> _logger =
        logger ?? NullLogger<BleAirDropAdvertiser>.Instance;

    private BluetoothLEAdvertisementPublisher? _publisher;

    public AdvertiserState State { get; private set; } = AdvertiserState.Stopped;

    /// <summary>Se dispara cuando cambia el estado del emisor.</summary>
    public event Action<AdvertiserState>? StateChanged;

    /// <summary>Empieza a emitir el anuncio de AirDrop.</summary>
    /// <param name="advertisement">
    /// Contenido del anuncio. Por defecto se emite sin identidad, que es lo apropiado en modo
    /// "Todos" y no expone los hashes de contacto del usuario.
    /// </param>
    public bool Start(AirDropAdvertisement? advertisement = null)
    {
        if (_publisher is not null)
        {
            return State == AdvertiserState.Started;
        }

        advertisement ??= AirDropAdvertisement.Anonymous();
        var payload = ContinuityAdvertisement.BuildManufacturerPayload(advertisement);

        try
        {
            var manufacturerData = new BluetoothLEManufacturerData
            {
                CompanyId = AirDropAdvertisement.AppleCompanyId,
                // El CompanyId va aparte, así que aquí solo la carga del mensaje.
                Data = CreateBuffer(payload.AsSpan(2).ToArray()),
            };

            _publisher = new BluetoothLEAdvertisementPublisher();
            _publisher.Advertisement.ManufacturerData.Add(manufacturerData);
            _publisher.StatusChanged += OnStatusChanged;
            _publisher.Start();

            _logger.LogInformation(
                "Anuncio BLE de AirDrop iniciado ({Bytes} bytes de datos de fabricante Apple)",
                payload.Length);

            return true;
        }
        catch (Exception ex)
        {
            // Sin adaptador Bluetooth, o con el radio apagado, esto lanza. No es un error fatal:
            // el resto de la aplicación funciona igual.
            _logger.LogWarning(ex, "No se pudo iniciar el anuncio BLE");
            SetState(AdvertiserState.Unavailable);
            _publisher = null;
            return false;
        }
    }

    public void Stop()
    {
        if (_publisher is null)
        {
            return;
        }

        try
        {
            _publisher.StatusChanged -= OnStatusChanged;
            _publisher.Stop();
            _logger.LogInformation("Anuncio BLE de AirDrop detenido");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallo deteniendo el anuncio BLE");
        }
        finally
        {
            _publisher = null;
            SetState(AdvertiserState.Stopped);
        }
    }

    private void OnStatusChanged(
        BluetoothLEAdvertisementPublisher sender,
        BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
    {
        var state = args.Status switch
        {
            BluetoothLEAdvertisementPublisherStatus.Started => AdvertiserState.Started,
            BluetoothLEAdvertisementPublisherStatus.Aborted => AdvertiserState.Aborted,
            BluetoothLEAdvertisementPublisherStatus.Stopped => AdvertiserState.Stopped,
            _ => State,
        };

        if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
        {
            // Ocurre cuando el adaptador no admite el anuncio o el radio se apaga.
            _logger.LogWarning("El anuncio BLE fue abortado por el sistema: {Error}", args.Error);
        }

        SetState(state);
    }

    private void SetState(AdvertiserState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private static IBuffer CreateBuffer(byte[] data)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        return writer.DetachBuffer();
    }

    public void Dispose() => Stop();
}
