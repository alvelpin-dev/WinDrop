using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDrop.Services;

/// <summary>Quién puede ver este equipo.</summary>
public enum DeviceVisibility
{
    /// <summary>Visible para cualquiera en la red.</summary>
    Everyone,

    /// <summary>
    /// Solo contactos. <b>No implementable</b>: exige un certificado de identidad de Apple ID
    /// firmado por Apple (docs/01 §6.5). Se conserva en el modelo para poder explicarlo en la
    /// interfaz en lugar de ocultar la limitación.
    /// </summary>
    ContactsOnly,

    /// <summary>No se anuncia ni se aceptan transferencias.</summary>
    Off,
}

/// <summary>Preferencias del usuario, persistidas en disco.</summary>
public sealed class AppSettings
{
    /// <summary>Nombre con el que aparece este equipo ante los demás.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Carpeta donde se guardan los ficheros recibidos.</summary>
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;

    public DeviceVisibility Visibility { get; set; } = DeviceVisibility.Everyone;

    /// <summary>Si se pide confirmación antes de aceptar cada transferencia.</summary>
    /// <remarks>
    /// Siempre activo y no configurable desde la interfaz: aceptar ficheros sin consentimiento
    /// convertiría el equipo en un buzón abierto para cualquiera en la red.
    /// </remarks>
    [JsonIgnore]
    public bool AlwaysAsk => true;

    public bool ShowNotifications { get; set; } = true;

    public bool PlaySound { get; set; } = true;

    /// <summary>Si se arranca la recepción automáticamente al abrir la aplicación.</summary>
    public bool StartReceivingOnLaunch { get; set; } = true;

    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "AirDrop");

    /// <summary>Directorio de datos de la aplicación.</summary>
    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinDrop");
}

/// <summary>Carga y guarda las preferencias en JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = Path.Combine(AppSettings.DataFolder, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(_path), SerializerOptions);

                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un fichero de preferencias corrupto no debe impedir arrancar: se vuelve a los
            // valores por defecto y se sobrescribirá al guardar.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Guardar preferencias es best-effort: perderlas no justifica tirar la aplicación.
        }
    }
}
