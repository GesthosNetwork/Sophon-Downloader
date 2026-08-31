using System.Net.Http.Headers;

namespace SophonDownloader.Services;

public sealed class LegacyArchivePart
{
    public string Url { get; }
    public long Length { get; }

    public LegacyArchivePart(string url, long length)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty.", nameof(url));

        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Url = url;
        Length = length;
    }
}

public sealed class LegacyArchiveStream : Stream
{
    private const int CacheSize = 4 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly List<LegacyArchivePart> _parts;
    private readonly long[] _offsets;
    private readonly long _length;

    private byte[] _cache = [];
    private int _cacheLength;
    private long _cacheStart = -1;
    private long _position;
    private bool _disposed;

    public LegacyArchiveStream(
        HttpClient httpClient,
        IReadOnlyList<LegacyArchivePart> parts)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (parts is null || parts.Count == 0)
            throw new ArgumentException("At least one archive part is required.", nameof(parts));

        _parts = parts.ToList();
        _offsets = new long[_parts.Count];

        long offset = 0;

        for (int i = 0; i < _parts.Count; i++)
        {
            _offsets[i] = offset;
            checked { offset += _parts[i].Length; }
        }

        _length = offset;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer is null)
            throw new ArgumentNullException(nameof(buffer));

        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException();

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (buffer.Length == 0 || _position >= _length)
            return 0;

        int totalRead = 0;

        while (buffer.Length > 0 && _position < _length)
        {
            int partIndex = FindPart(_position);
            long partStart = _offsets[partIndex];
            LegacyArchivePart part = _parts[partIndex];
            long localPosition = _position - partStart;
            long remaining = part.Length - localPosition;

            if (remaining <= 0)
                break;

            int requested = (int)Math.Min(buffer.Length, remaining);

            EnsureCached(partIndex, localPosition, requested);

            int cacheOffset = checked((int)(localPosition - _cacheStart));
            int available = _cacheLength - cacheOffset;

            if (available <= 0)
            {
                throw new EndOfStreamException("The remote archive returned no readable data.");
            }

            int copyCount = Math.Min(requested, available);

            _cache.AsSpan(cacheOffset, copyCount).CopyTo(buffer);
            _position += copyCount;
            totalRead += copyCount;
            buffer = buffer[copyCount..];
        }

        return totalRead;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        int read = Read(buffer);

        return read == 0 ? -1 : buffer[0];
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _length)
            throw new IOException("Seek position is outside the archive.");

        _position = target;
        return _position;
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException();

    public override Task WriteAsync(
        byte[] buffer, int offset, int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private void EnsureCached(
        int partIndex, long localPosition, int requested)
    {
        long cacheEnd = _cacheStart >= 0 ? _cacheStart + _cacheLength : -1;

        if (_cacheStart >= 0 &&
            localPosition >= _cacheStart &&
            localPosition < cacheEnd &&
            requested <= cacheEnd - localPosition)
        { return; }

        LegacyArchivePart part = _parts[partIndex];
        long remaining = part.Length - localPosition;

        int length = (int)Math.Min(
            Math.Max(remaining, requested),
            CacheSize);

        if (length <= 0)
            throw new EndOfStreamException();

        byte[] data = FetchRange(
            part.Url, localPosition,
            localPosition + length - 1, length);

        _cache = data;
        _cacheLength = data.Length;
        _cacheStart = localPosition;
    }

    private byte[] FetchRange(
        string url, long start,  long end, int expectedLength)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using HttpResponseMessage response = _httpClient.Send(
            request, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            using Stream stream = response.Content.ReadAsStream();
            return ReadExpectedBytes(stream, expectedLength);
        }

        if (response.StatusCode == HttpStatusCode.OK && start == 0)
        {
            using Stream stream = response.Content.ReadAsStream();
            return ReadExpectedBytes(stream, expectedLength);
        }

        throw new IOException(
            $"The archive server did not honor the requested byte range. " +
            $"HTTP {(int)response.StatusCode}.");
    }

    private static byte[] ReadExpectedBytes(
        Stream stream, int expectedLength)
    {
        var result = new byte[expectedLength];
        int total = 0;

        while (total < expectedLength)
        {
            int read = stream.Read(result, total, expectedLength - total);

            if (read == 0) break;
            total += read;
        }

        if (total == expectedLength)
            return result;

        if (total <= 0)
            throw new EndOfStreamException("The archive server returned no data.");

        Array.Resize(ref result, total);
        return result;
    }

    private int FindPart(long position)
    {
        int low = 0;
        int high = _parts.Count - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            long start = _offsets[middle];
            long end = start + _parts[middle].Length;

            if (position < start)
                high = middle - 1;
            else if (position >= end)
                low = middle + 1;
            else
                return middle;
        }

        throw new IOException("Unable to locate the requested archive position.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LegacyArchiveStream));
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _cache = [];
        _cacheLength = 0;
        _cacheStart = -1;

        base.Dispose(disposing);
    }
}
