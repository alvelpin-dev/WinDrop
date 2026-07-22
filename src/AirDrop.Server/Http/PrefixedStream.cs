namespace AirDrop.Server.Http;

/// <summary>
/// Stream de solo lectura que antepone un buffer ya leído al resto del stream original.
/// </summary>
/// <remarks>
/// Hace falta porque el cuerpo de <c>/Upload</c> llega comprimido con gzip <b>sin la cabecera
/// <c>Content-Encoding</c></b> (docs/01 §6.2). La única forma de saberlo es mirar los dos primeros
/// bytes en busca del magic de gzip, pero el cuerpo de una petición HTTP no permite retroceder.
/// Esta clase devuelve al flujo los bytes ya consumidos en la inspección.
/// </remarks>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = ReadFromPrefix(buffer);
        return fromPrefix > 0 ? fromPrefix : inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var fromPrefix = ReadFromPrefix(buffer.Span);
        return fromPrefix > 0
            ? fromPrefix
            : await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Sirve lo que quede del prefijo. Devuelve 0 cuando se ha agotado, sin tocar el stream
    /// interno: no se mezclan las dos fuentes en una misma lectura para no complicar el contrato.
    /// </summary>
    private int ReadFromPrefix(Span<byte> buffer)
    {
        var remaining = prefix.Length - _prefixPosition;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var count = Math.Min(remaining, buffer.Length);
        prefix.AsSpan(_prefixPosition, count).CopyTo(buffer);
        _prefixPosition += count;
        return count;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
