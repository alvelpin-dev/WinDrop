using System.Text;

namespace AirDrop.Core.Archives;

/// <summary>
/// Lector de archivos CPIO en streaming.
/// </summary>
/// <remarks>
/// <para>
/// El cuerpo de <c>POST /Upload</c> es un archivo CPIO comprimido con gzip. Se lee en streaming
/// porque una transferencia de AirDrop puede traer vídeos de varios gigabytes: cargar el archivo
/// entero en memoria no es viable.
/// </para>
/// <para>
/// Soporta las dos variantes observadas en AirDrop —<c>odc</c> (octal) y <c>newc</c> (hexadecimal,
/// alineada a 4 bytes)— detectándolas por el magic de cada cabecera. La investigación del protocolo
/// no permitió determinar con certeza cuál usa cada versión de iOS, así que se aceptan ambas.
/// </para>
/// <para>Uso:</para>
/// <code>
/// await using var reader = new CpioReader(gzipStream);
/// while (await reader.MoveNextAsync(ct) is { } entry)
/// {
///     await reader.CopyEntryDataToAsync(destination, ct);
/// }
/// </code>
/// </remarks>
public sealed class CpioReader(Stream stream, bool leaveOpen = false) : IAsyncDisposable
{
    private const int OdcHeaderSize = 76;
    private const int NewcHeaderSize = 110;

    /// <summary>Nombre máximo aceptado. Un nombre desmesurado solo puede venir de un archivo manipulado.</summary>
    private const int MaxNameLength = 4096;

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    private long _remainingInCurrentEntry;
    private int _paddingAfterCurrentEntry;
    private bool _finished;

    /// <summary>Formato detectado en la primera cabecera leída.</summary>
    public CpioFormat? DetectedFormat { get; private set; }

    /// <summary>Entrada actual, o <c>null</c> si aún no se ha leído ninguna o ya se acabó el archivo.</summary>
    public CpioEntry? Current { get; private set; }

    /// <summary>
    /// Avanza a la siguiente entrada, saltando los datos de la anterior que no se hayan consumido.
    /// </summary>
    /// <returns>La siguiente entrada, o <c>null</c> al alcanzar el terminador <c>TRAILER!!!</c>.</returns>
    public async ValueTask<CpioEntry?> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return null;
        }

        await SkipRemainingDataAsync(cancellationToken).ConfigureAwait(false);

        var magic = new byte[6];
        if (!await TryReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false))
        {
            // Fin de stream limpio sin TRAILER: archivo truncado.
            throw new CpioFormatException("El archivo CPIO terminó sin la entrada TRAILER!!!.");
        }

        var format = ParseMagic(magic);
        DetectedFormat ??= format;

        var entry = format is CpioFormat.Odc
            ? await ReadOdcEntryAsync(cancellationToken).ConfigureAwait(false)
            : await ReadNewcEntryAsync(cancellationToken).ConfigureAwait(false);

        if (entry.Name == CpioEntry.TrailerName)
        {
            _finished = true;
            Current = null;
            return null;
        }

        Current = entry;
        _remainingInCurrentEntry = entry.DataLength;
        _paddingAfterCurrentEntry = format is CpioFormat.Odc
            ? 0
            : PaddingTo4(entry.DataLength);

        return entry;
    }

    /// <summary>Copia el contenido de la entrada actual al destino, informando del progreso.</summary>
    /// <param name="destination">Destino de los datos.</param>
    /// <param name="onProgress">Se invoca con el número acumulado de bytes copiados de esta entrada.</param>
    public async Task CopyEntryDataToAsync(
        Stream destination,
        IProgress<long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (Current is null)
        {
            throw new InvalidOperationException(
                $"No hay entrada activa. Llama a {nameof(MoveNextAsync)} primero.");
        }

        var buffer = new byte[81_920];
        long copied = 0;

        while (_remainingInCurrentEntry > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, _remainingInCurrentEntry);
            var read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new CpioFormatException(
                    $"El archivo se cortó dentro de '{Current.Name}': faltan " +
                    $"{_remainingInCurrentEntry} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            _remainingInCurrentEntry -= read;
            copied += read;
            onProgress?.Report(copied);
        }
    }

    private static CpioFormat ParseMagic(ReadOnlySpan<byte> magic)
    {
        var text = Encoding.ASCII.GetString(magic);
        return text switch
        {
            "070707" => CpioFormat.Odc,
            "070701" => CpioFormat.Newc,
            "070702" => CpioFormat.NewcCrc,
            _ => throw new CpioFormatException(
                $"Magic de cabecera CPIO desconocido: '{text}'. " +
                "Se esperaba 070707 (odc), 070701 o 070702 (newc)."),
        };
    }

    private async ValueTask<CpioEntry> ReadOdcEntryAsync(CancellationToken cancellationToken)
    {
        // Cabecera odc: campos octales de ancho fijo, sin separadores. El magic ya se consumió.
        var header = new byte[OdcHeaderSize - 6];
        await ReadExactlyOrThrowAsync(header, cancellationToken).ConfigureAwait(false);

        // Offsets relativos al final del magic: c_dev(0) c_ino(6) c_mode(12) c_uid(18) c_gid(24)
        // c_nlink(30) c_rdev(36) c_mtime(42,11) c_namesize(53) c_filesize(59,11) = 70 bytes.
        var fields = header.AsSpan();
        var mode = (int)ParseNumber(fields.Slice(12, 6), 8, "c_mode");
        var mtime = ParseNumber(fields.Slice(42, 11), 8, "c_mtime");
        var nameSize = (int)ParseNumber(fields.Slice(53, 6), 8, "c_namesize");
        var fileSize = ParseNumber(fields.Slice(59, 11), 8, "c_filesize");

        var name = await ReadNameAsync(nameSize, cancellationToken).ConfigureAwait(false);

        return new CpioEntry(
            name,
            mode,
            fileSize,
            DateTimeOffset.FromUnixTimeSeconds(mtime));
    }

    private async ValueTask<CpioEntry> ReadNewcEntryAsync(CancellationToken cancellationToken)
    {
        // Cabecera newc: 13 campos hexadecimales de 8 caracteres. El magic ya se consumió.
        var header = new byte[NewcHeaderSize - 6];
        await ReadExactlyOrThrowAsync(header, cancellationToken).ConfigureAwait(false);

        // Offsets relativos al final del magic: c_ino(0) c_mode(8) c_uid(16) c_gid(24) c_nlink(32)
        // c_mtime(40) c_filesize(48) c_devmajor(56) c_devminor(64) c_rdevmajor(72) c_rdevminor(80)
        // c_namesize(88) c_check(96) = 104 bytes.
        var fields = header.AsSpan();
        var mode = (int)ParseNumber(fields.Slice(8, 8), 16, "c_mode");
        var mtime = ParseNumber(fields.Slice(40, 8), 16, "c_mtime");
        var fileSize = ParseNumber(fields.Slice(48, 8), 16, "c_filesize");
        var nameSize = (int)ParseNumber(fields.Slice(88, 8), 16, "c_namesize");

        var name = await ReadNameAsync(nameSize, cancellationToken).ConfigureAwait(false);

        // En newc el nombre se rellena para que los datos empiecen en múltiplo de 4, contando
        // desde el inicio de la cabecera.
        await SkipAsync(PaddingTo4(NewcHeaderSize + nameSize), cancellationToken).ConfigureAwait(false);

        return new CpioEntry(
            name,
            mode,
            fileSize,
            DateTimeOffset.FromUnixTimeSeconds(mtime));
    }

    private async ValueTask<string> ReadNameAsync(int nameSize, CancellationToken cancellationToken)
    {
        if (nameSize is <= 0 or > MaxNameLength)
        {
            throw new CpioFormatException($"Longitud de nombre fuera de rango: {nameSize}.");
        }

        var buffer = new byte[nameSize];
        await ReadExactlyOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);

        // c_namesize incluye el terminador NUL.
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        // Los nombres de fichero de iOS traen acentos y emoji: UTF-8, no ASCII.
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static long ParseNumber(ReadOnlySpan<byte> field, int numericBase, string fieldName)
    {
        long value = 0;
        var sawDigit = false;

        foreach (var b in field)
        {
            // Algunos escritores rellenan con espacios o NUL en vez de ceros a la izquierda.
            if (b is (byte)' ' or 0)
            {
                continue;
            }

            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };

            if (digit < 0 || digit >= numericBase)
            {
                throw new CpioFormatException(
                    $"Carácter inválido '{(char)b}' en el campo {fieldName} (base {numericBase}).");
            }

            value = (value * numericBase) + digit;
            sawDigit = true;
        }

        if (!sawDigit)
        {
            throw new CpioFormatException($"Campo {fieldName} vacío.");
        }

        return value;
    }

    private static int PaddingTo4(long position) => (int)((4 - (position % 4)) % 4);

    private async ValueTask SkipRemainingDataAsync(CancellationToken cancellationToken)
    {
        if (_remainingInCurrentEntry > 0)
        {
            await SkipAsync(_remainingInCurrentEntry, cancellationToken).ConfigureAwait(false);
            _remainingInCurrentEntry = 0;
        }

        if (_paddingAfterCurrentEntry > 0)
        {
            await SkipAsync(_paddingAfterCurrentEntry, cancellationToken).ConfigureAwait(false);
            _paddingAfterCurrentEntry = 0;
        }
    }

    private async ValueTask SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        var buffer = new byte[(int)Math.Min(count, 81_920)];
        while (count > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, count);
            var read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new CpioFormatException("El archivo CPIO terminó de forma inesperada.");
            }

            count -= read;
        }
    }

    private async ValueTask ReadExactlyOrThrowAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (!await TryReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new CpioFormatException("Cabecera CPIO incompleta: el stream terminó antes de tiempo.");
        }
    }

    private async ValueTask<bool> TryReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
