using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinDrop.Services;

/// <summary>
/// Registro a fichero de texto.
/// </summary>
/// <remarks>
/// <para>
/// Los logs son la única forma de diagnosticar un fallo de interoperabilidad: cuando una
/// transferencia no funciona con otro dispositivo, lo que hace falta es el rastro exacto del
/// descubrimiento y del intercambio, no un mensaje genérico en pantalla.
/// </para>
/// <para>
/// Se implementa a mano en lugar de traer Serilog o NLog: son dos ficheros de código frente a
/// varios megabytes añadidos al ejecutable portable.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>A partir de este tamaño el fichero se rota.</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLoggerProvider()
    {
        var folder = Path.Combine(AppSettings.DataFolder, "logs");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "windrop.log");

        RotateIfNeeded();
    }

    /// <summary>Ruta del fichero de log actual, para poder abrirlo desde la interfaz.</summary>
    public string LogPath => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            // Solo la última parte del nombre de tipo: el namespace completo hace las líneas
            // ilegibles sin aportar nada.
            .Append(category[(category.LastIndexOf('.') + 1)..])
            .Append(": ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    "));
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No poder escribir el log jamás debe tumbar la aplicación.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return;
            }

            var previous = Path.ChangeExtension(_path, ".previous.log");
            File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort.
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        // Nada que liberar: cada escritura abre y cierra el fichero.
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
