using System.Globalization;
using System.Text;

namespace AirDrop.Core.Archives;

/// <summary>
/// Escritor de archivos CPIO en streaming, para construir el cuerpo de <c>POST /Upload</c>.
/// </summary>
/// <remarks>
/// <para>
/// El formato por defecto es <see cref="CpioFormat.Odc"/>, que es el que se ha documentado en las
/// transferencias de AirDrop cuando no se negocia DVZip.
/// </para>
/// <para>
/// ⚠️ PENDIENTE DE VERIFICACIÓN EMPÍRICA (test 5 del plan de validación): no se ha podido
/// confirmar contra tráfico real cuál de las dos variantes espera cada versión de iOS. Por eso el
/// formato es configurable en vez de estar fijado en el código.
/// </para>
/// </remarks>
public sealed class CpioWriter(
    Stream stream,
    CpioFormat format = CpioFormat.Odc,
    bool leaveOpen = false) : IAsyncDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private bool _trailerWritten;
    private long _position;

    /// <summary>Escribe la cabecera de una entrada. Después hay que escribir exactamente sus datos.</summary>
    public async Task WriteEntryHeaderAsync(
        CpioEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_trailerWritten, this);

        await WriteHeaderAsync(entry.Name, entry.Mode, entry.DataLength, entry.ModifiedTime, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Escribe una entrada completa a partir de un stream de origen.</summary>
    /// <param name="onProgress">Se invoca con el número acumulado de bytes escritos de esta entrada.</param>
    public async Task WriteEntryAsync(
        CpioEntry entry,
        Stream content,
        IProgress<long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        await WriteEntryHeaderAsync(entry, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81_920];
        long written = 0;

        while (written < entry.DataLength)
        {
            var toRead = (int)Math.Min(buffer.Length, entry.DataLength - written);
            var read = await content.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // La cabecera ya declaró el tamaño: si el origen da menos, el archivo queda corrupto
                // y hay que fallar de forma explícita en vez de emitir basura.
                throw new CpioFormatException(
                    $"El origen de '{entry.Name}' se agotó tras {written} de {entry.DataLength} bytes.");
            }

            await _stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            _position += read;
            onProgress?.Report(written);
        }

        await WriteDataPaddingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Escribe la entrada terminadora <c>TRAILER!!!</c> que cierra el archivo. Es obligatoria:
    /// sin ella el receptor considera el archivo truncado.
    /// </summary>
    public async Task WriteTrailerAsync(CancellationToken cancellationToken = default)
    {
        if (_trailerWritten)
        {
            return;
        }

        await WriteHeaderAsync(
            CpioEntry.TrailerName,
            mode: 0,
            dataLength: 0,
            DateTimeOffset.UnixEpoch,
            cancellationToken).ConfigureAwait(false);

        _trailerWritten = true;
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteHeaderAsync(
        string name,
        int mode,
        long dataLength,
        DateTimeOffset modifiedTime,
        CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var nameSize = nameBytes.Length + 1;   // incluye el NUL terminador
        var mtime = Math.Max(0, modifiedTime.ToUnixTimeSeconds());

        var header = format is CpioFormat.Odc
            ? BuildOdcHeader(mode, dataLength, nameSize, mtime)
            : BuildNewcHeader(mode, dataLength, nameSize, mtime);

        await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
        _stream.WriteByte(0);
        _position += header.Length + nameSize;

        if (format is not CpioFormat.Odc)
        {
            // En newc los datos deben empezar en un múltiplo de 4.
            await WritePaddingAsync(PaddingTo4(_position), cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] BuildOdcHeader(int mode, long dataLength, int nameSize, long mtime)
    {
        // 76 bytes: magic (6) + 8 campos de 6 + mtime de 11 + namesize de 6 + filesize de 11.
        var builder = new StringBuilder(76);
        builder.Append("070707");
        builder.Append(Octal(0, 6));            // c_dev
        builder.Append(Octal(0, 6));            // c_ino
        builder.Append(Octal(mode, 6));         // c_mode
        builder.Append(Octal(0, 6));            // c_uid
        builder.Append(Octal(0, 6));            // c_gid
        builder.Append(Octal(1, 6));            // c_nlink
        builder.Append(Octal(0, 6));            // c_rdev
        builder.Append(Octal(mtime, 11));       // c_mtime
        builder.Append(Octal(nameSize, 6));     // c_namesize
        builder.Append(Octal(dataLength, 11));  // c_filesize

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] BuildNewcHeader(int mode, long dataLength, int nameSize, long mtime)
    {
        // 110 bytes: magic (6) + 13 campos hexadecimales de 8.
        var builder = new StringBuilder(110);
        builder.Append("070701");
        builder.Append(Hex(0, 8));            // c_ino
        builder.Append(Hex(mode, 8));         // c_mode
        builder.Append(Hex(0, 8));            // c_uid
        builder.Append(Hex(0, 8));            // c_gid
        builder.Append(Hex(1, 8));            // c_nlink
        builder.Append(Hex(mtime, 8));        // c_mtime
        builder.Append(Hex(dataLength, 8));   // c_filesize
        builder.Append(Hex(0, 8));            // c_devmajor
        builder.Append(Hex(0, 8));            // c_devminor
        builder.Append(Hex(0, 8));            // c_rdevmajor
        builder.Append(Hex(0, 8));            // c_rdevminor
        builder.Append(Hex(nameSize, 8));     // c_namesize
        builder.Append(Hex(0, 8));            // c_check (siempre 0 salvo en la variante con CRC)

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string Octal(long value, int width) =>
        Convert.ToString(value, 8).PadLeft(width, '0')[^width..];

    private static string Hex(long value, int width) =>
        value.ToString("X", CultureInfo.InvariantCulture).PadLeft(width, '0')[^width..];

    private static int PaddingTo4(long position) => (int)((4 - (position % 4)) % 4);

    private async Task WriteDataPaddingAsync(CancellationToken cancellationToken)
    {
        if (format is CpioFormat.Odc)
        {
            return;   // odc no alinea
        }

        await WritePaddingAsync(PaddingTo4(_position), cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePaddingAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        await _stream.WriteAsync(new byte[count], cancellationToken).ConfigureAwait(false);
        _position += count;
    }

    public async ValueTask DisposeAsync()
    {
        await WriteTrailerAsync().ConfigureAwait(false);

        if (!leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
