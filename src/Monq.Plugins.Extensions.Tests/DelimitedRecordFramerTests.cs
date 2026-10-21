using Monq.Plugins.Extensions.Net.Tcp;
using System.Text;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public class DelimitedRecordFramerTests
{
    static readonly byte[] _delimiter = "\r\n"u8.ToArray();

    [Fact]
    public void Append_CombinesRecordAndDelimiterAcrossBlocks()
    {
        using var framer = new DelimitedRecordFramer(_delimiter, 32);

        framer.Append("first\r"u8);
        Assert.False(framer.TryRead(out _, out _));

        framer.Append("\nsecond\r\n"u8);

        Assert.True(framer.TryRead(out var first, out var firstSkipped));
        Assert.False(firstSkipped);
        Assert.Equal("first", Encoding.UTF8.GetString(first!));
        Assert.True(framer.TryRead(out var second, out var secondSkipped));
        Assert.False(secondSkipped);
        Assert.Equal("second", Encoding.UTF8.GetString(second!));
        Assert.False(framer.TryRead(out _, out _));
    }

    [Fact]
    public void Append_DiscardsOversizedRecordAndContinuesAfterDelimiter()
    {
        using var framer = new DelimitedRecordFramer(_delimiter, 5);

        framer.Append("too-long-record\r"u8);
        Assert.True(framer.TryRead(out var oversizedRecord, out var oversizedRecordSkipped));
        Assert.Null(oversizedRecord);
        Assert.True(oversizedRecordSkipped);

        framer.Append("\nok\r\n"u8);
        Assert.True(framer.TryRead(out var record, out var recordSkipped));
        Assert.False(recordSkipped);
        Assert.Equal("ok", Encoding.UTF8.GetString(record!));
    }

    [Fact]
    public void Instances_KeepStreamStatesIndependent()
    {
        using var firstStream = new DelimitedRecordFramer(_delimiter, 32);
        using var secondStream = new DelimitedRecordFramer(_delimiter, 32);
        firstStream.Append("first"u8);
        secondStream.Append("second\r\n"u8);
        firstStream.Append("\r\n"u8);

        Assert.True(firstStream.TryRead(out var first, out _));
        Assert.True(secondStream.TryRead(out var second, out _));
        Assert.Equal("first", Encoding.UTF8.GetString(first!));
        Assert.Equal("second", Encoding.UTF8.GetString(second!));
    }

    [Fact]
    public void Constructor_CopiesDelimiter()
    {
        var delimiter = "\r\n"u8.ToArray();
        using var framer = new DelimitedRecordFramer(delimiter, 32);

        delimiter[0] = (byte)'x';
        framer.Append("record\r\n"u8);

        Assert.True(framer.TryRead(out var record, out _));
        Assert.Equal("record", Encoding.UTF8.GetString(record!));
    }

    [Fact]
    public void Constructor_RejectsEmptyDelimiter()
        => Assert.Throws<ArgumentException>(() => new DelimitedRecordFramer([], 32));

    [Fact]
    public void Constructor_RejectsNonPositiveMaximumRecordSize()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DelimitedRecordFramer(_delimiter, 0));

    [Fact]
    public void Operations_RejectUseAfterDispose()
    {
        var framer = new DelimitedRecordFramer(_delimiter, 32);
        framer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => framer.Append("record"u8));
        Assert.Throws<ObjectDisposedException>(() => framer.TryRead(out _, out _));
    }
}
