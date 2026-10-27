using System.Buffers;

namespace Monq.Plugins.Extensions.Net.Tcp;

/// <summary>
/// Reassembles delimiter-separated records from arbitrarily segmented byte sequences.
/// </summary>
/// <remarks>
/// The delimiter is excluded from returned records. This type is not thread-safe.
/// </remarks>
public sealed class DelimitedRecordFramer : IDisposable
{
    readonly byte[] _delimiter;
    readonly int _maxRecordSize;

    byte[] _buffer;
    int _writtenCount;
    bool _discardingOversizedRecord;
    bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelimitedRecordFramer"/> class.
    /// </summary>
    /// <param name="delimiter">The non-empty byte sequence that terminates each record.</param>
    /// <param name="maxRecordSize">The maximum permitted record size, in bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="delimiter"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxRecordSize"/> is less than one.
    /// </exception>
    public DelimitedRecordFramer(ReadOnlySpan<byte> delimiter, int maxRecordSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordSize, 1);

        if (delimiter.IsEmpty)
            throw new ArgumentException("Delimiter must not be empty.", nameof(delimiter));

        _delimiter = delimiter.ToArray();
        _maxRecordSize = maxRecordSize;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxRecordSize + _delimiter.Length, 4096));
    }

    /// <summary>
    /// Appends a block of bytes to the buffered input.
    /// </summary>
    /// <param name="data">The bytes to append.</param>
    /// <exception cref="ObjectDisposedException">The framer has been disposed.</exception>
    public void Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureCapacity(_writtenCount + data.Length);
        data.CopyTo(_buffer.AsSpan(_writtenCount));
        _writtenCount += data.Length;
    }

    /// <summary>
    /// Attempts to read the next complete record or report a discarded oversized record.
    /// </summary>
    /// <param name="record">
    /// When this method returns <see langword="true"/>, contains the next complete record;
    /// otherwise, or when an oversized record was discarded, <see langword="null"/>.
    /// </param>
    /// <param name="oversizedRecordSkipped">
    /// When this method returns <see langword="true"/>, indicates whether an oversized record
    /// was discarded instead of returning a record.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a complete record was returned or an oversized record was
    /// discarded; otherwise, <see langword="false"/> when more data is required.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The framer has been disposed.</exception>
    public bool TryRead(out byte[]? record, out bool oversizedRecordSkipped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            var delimiterIndex = _buffer.AsSpan(0, _writtenCount).IndexOf(_delimiter);
            if (delimiterIndex < 0)
            {
                if (!_discardingOversizedRecord && _writtenCount > _maxRecordSize)
                {
                    _discardingOversizedRecord = true;
                    PreserveDelimiterPrefix();
                    record = null;
                    oversizedRecordSkipped = true;
                    return true;
                }

                record = null;
                oversizedRecordSkipped = false;
                return false;
            }

            if (_discardingOversizedRecord)
            {
                _discardingOversizedRecord = false;
                RemovePrefix(delimiterIndex + _delimiter.Length);
                continue;
            }

            if (delimiterIndex > _maxRecordSize)
            {
                RemovePrefix(delimiterIndex + _delimiter.Length);
                record = null;
                oversizedRecordSkipped = true;
                return true;
            }

            record = _buffer.AsSpan(0, delimiterIndex).ToArray();
            RemovePrefix(delimiterIndex + _delimiter.Length);
            oversizedRecordSkipped = false;
            return true;
        }
    }

    void PreserveDelimiterPrefix()
    {
        var length = Math.Min(_writtenCount, _delimiter.Length - 1);
        _buffer.AsSpan(_writtenCount - length, length).CopyTo(_buffer);
        _writtenCount = length;
    }

    void RemovePrefix(int length)
    {
        var remainingLength = _writtenCount - length;
        _buffer.AsSpan(length, remainingLength).CopyTo(_buffer);
        _writtenCount = remainingLength;
    }

    void EnsureCapacity(int requiredCapacity)
    {
        if (_buffer.Length >= requiredCapacity)
            return;

        var newBuffer = ArrayPool<byte>.Shared.Rent(requiredCapacity);
        _buffer.AsSpan(0, _writtenCount).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    /// <summary>
    /// Returns the rented buffer to the shared buffer pool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _disposed = true;
    }
}
