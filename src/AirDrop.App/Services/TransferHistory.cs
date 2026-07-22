using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDrop.Services;

public enum TransferDirection
{
    Received,
    Sent,
}

public enum TransferStatus
{
    Completed,
    Failed,
    Rejected,
    Cancelled,
}

/// <summary>Una entrada del historial de transferencias.</summary>
public sealed record TransferRecord
{
    public required DateTimeOffset Timestamp { get; init; }

    public required TransferDirection Direction { get; init; }

    public required TransferStatus Status { get; init; }

    /// <summary>Nombre del otro dispositivo.</summary>
    public required string PeerName { get; init; }

    public required IReadOnlyList<string> FileNames { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>Carpeta donde quedaron los ficheros, si se recibieron.</summary>
    public string? Folder { get; init; }

    /// <summary>Motivo del fallo, para poder mostrarlo sin tener que abrir los logs.</summary>
    public string? ErrorMessage { get; init; }

    public string Summary => FileNames.Count == 1
        ? FileNames[0]
        : $"{FileNames.Count} archivos";
}

/// <summary>
/// Historial de transferencias, persistido en disco.
/// </summary>
/// <remarks>
/// Se limita a las entradas más recientes: es un registro de actividad para el usuario, no un
/// archivo de auditoría, y dejarlo crecer sin límite acabaría ralentizando el arranque.
/// </remarks>
public sealed class TransferHistory
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = Path.Combine(AppSettings.DataFolder, "history.json");
    private readonly Lock _gate = new();

    /// <summary>Entradas, de la más reciente a la más antigua.</summary>
    public ObservableCollection<TransferRecord> Entries { get; } = [];

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<TransferRecord>>(
                File.ReadAllText(_path), SerializerOptions);

            if (loaded is null)
            {
                return;
            }

            Entries.Clear();
            foreach (var record in loaded.Take(MaxEntries))
            {
                Entries.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un historial ilegible se descarta: no es información crítica.
        }
    }

    public void Add(TransferRecord record)
    {
        Entries.Insert(0, record);

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        Save();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    private void Save()
    {
        // Se copia dentro del bloqueo porque la colección la modifica el hilo de interfaz
        // mientras el guardado puede ocurrir desde otro.
        List<TransferRecord> snapshot;
        lock (_gate)
        {
            snapshot = [.. Entries];
        }

        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort.
        }
    }
}
