using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinDrop.Services;

namespace WinDrop;

/// <summary>Visible cuando el valor es <c>false</c>.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible cuando la colección tiene elementos. Invertible pasando <c>Invert</c>.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value as int? ?? 0;
        var invert = parameter as string == "Invert";
        var hasItems = count > 0;

        return (invert ? !hasItems : hasItems) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formatea un tamaño en bytes de forma legible.</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0L,
        };

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Icono que representa el estado de una transferencia.</summary>
/// <remarks>
/// Se usan caracteres Unicode corrientes en lugar de una fuente de iconos: se ven igual en
/// cualquier equipo y no dependen de que Segoe MDL2 esté instalada.
/// </remarks>
public sealed class TransferStatusIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            TransferStatus.Completed => "✓",   // marca de verificación
            TransferStatus.Failed => "✕",      // aspa
            TransferStatus.Rejected => "⊘",    // círculo barrado
            TransferStatus.Cancelled => "⊘",
            _ => "•",                          // punto
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Color asociado al estado de una transferencia.</summary>
public sealed class TransferStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x88, 0x88, 0x88));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            TransferStatus.Completed => Success,
            TransferStatus.Failed => Error,
            _ => Neutral,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Expresa una fecha como tiempo transcurrido.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset timestamp)
        {
            return string.Empty;
        }

        var elapsed = DateTimeOffset.Now - timestamp;

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "hace un momento",
            { TotalMinutes: < 60 } => $"hace {(int)elapsed.TotalMinutes} min",
            { TotalHours: < 24 } => $"hace {(int)elapsed.TotalHours} h",
            { TotalDays: < 7 } => $"hace {(int)elapsed.TotalDays} d",
            _ => timestamp.ToString("d MMM yyyy", culture),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
