using Monq.Plugins.Extensions.Prometheus;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public sealed class PrometheusParserTests
{
    static readonly DateTimeOffset _timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1690000000000);

    // Test vectors cover the supported Prometheus text exposition syntax.

    [Fact]
    public void TryParseLine_FullLine()
    {
        var ok = PrometheusParser.TryParseLine("http_requests_total{method=\"post\",code=\"200\"} 1027 1395066363000", out var m);

        Assert.True(ok);
        Assert.Equal("http_requests_total", m!.Name);
        Assert.Equal(new Dictionary<string, string> { ["method"] = "post", ["code"] = "200" }, m.Labels);
        Assert.Equal(1027.0, m.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1395066363000), m.Timestamp);
    }

    [Fact]
    public void TryParseLine_MultipleSpacesBetweenTokens()
    {
        var ok = PrometheusParser.TryParseLine("http_requests_total{method=\"post\",code=\"400\"}    3 1395066363000", out var m);

        Assert.True(ok);
        Assert.Equal(3.0, m!.Value);
    }

    [Fact]
    public void TryParseLine_WithoutLabelsAndTimestamp()
    {
        var ok = PrometheusParser.TryParseLine("metric_without_timestamp_and_labels 12.47", out var m);

        Assert.True(ok);
        Assert.Empty(m!.Labels);
        Assert.Equal(12.47, m.Value);
        Assert.Null(m.Timestamp);
    }

    [Fact]
    public void TryParseLine_PositiveInf()
    {
        // Line from the Prometheus specification corpus.
        var ok = PrometheusParser.TryParseLine("something_weird{problem=\"division by zero\"} +Inf -3982045", out var m);

        Assert.True(ok);
        Assert.Equal(double.PositiveInfinity, m!.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(-3982045), m.Timestamp);
    }

    [Fact]
    public void TryParseLine_NegativeInf()
    {
        var ok = PrometheusParser.TryParseLine("something_weird{problem=\"division by zero\"} -Inf -3982045", out var m);

        Assert.True(ok);
        Assert.Equal(double.NegativeInfinity, m!.Value);
    }

    [Fact]
    // Escaped values from the Prometheus parser corpus.
    public void TryParseLine_EscapesLabelValues()
    {
        var ok = PrometheusParser.TryParseLine(
            "msdos_file_access_time_seconds{path=\"C:\\\\DIR\\\\FILE.TXT\",error=\"Cannot find file:\\n \\\"FILE.TXT\\\" \"} 1.458255915e9",
            out var m);

        Assert.True(ok);
        Assert.Equal(
            new Dictionary<string, string> { ["path"] = @"C:\DIR\FILE.TXT", ["error"] = "Cannot find file:\n \"FILE.TXT\" " },
            m!.Labels);
        Assert.Equal(1.458255915e9, m.Value);
        Assert.Null(m.Timestamp);
    }

    [Fact]
    public void TryParseLine_ScientificNotation()
    {
        var ok = PrometheusParser.TryParseLine("rpc_duration_seconds_sum 1.7560473e+07", out var m);

        Assert.True(ok);
        Assert.Equal(17560473.0, m!.Value);
    }

    [Fact]
    public void TryParseLine_PlusInfAsLabelValue()
    {
        var ok = PrometheusParser.TryParseLine("http_request_duration_seconds_bucket{le=\"+Inf\"} 144320", out var m);

        Assert.True(ok);
        Assert.Equal(new Dictionary<string, string> { ["le"] = "+Inf" }, m!.Labels);
        Assert.Equal(144320.0, m.Value);
    }

    [Fact]
    public void TryParseLine_NanLiteral()
    {
        var ok = PrometheusParser.TryParseLine("metric_with_nan Nan", out var m);

        Assert.True(ok);
        Assert.Null(m!.Value);
    }

    // Additional accepted input forms that are normalized when rendered.

    [Fact]
    public void TryParseLine_NaNIsAccepted()
    {
        // Both supported NaN spellings are represented by a null value.
        var ok = PrometheusParser.TryParseLine("metric_with_nan NaN", out var m);

        Assert.True(ok);
        Assert.Null(m!.Value);
    }

    [Fact]
    public void TryParseLine_TrailingCommaInLabels()
    {
        // Формат, из-за которого metricsfromstatictargets держал хак line.Replace(",}", "}")
        var ok = PrometheusParser.TryParseLine("m{a=\"1\",} 2", out var m);

        Assert.True(ok);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, m!.Labels);
        Assert.Equal(2.0, m.Value);
    }

    [Fact]
    public void TryParseLine_TrailingWhitespace()
    {
        Assert.True(PrometheusParser.TryParseLine("m 1 2   ", out _));
        Assert.True(PrometheusParser.TryParseLine("m 1 2\r", out _));
        Assert.True(PrometheusParser.TryParseLine("    m 1", out _));
    }

    [Fact]
    public void TryParseLine_NoSpaceBetweenLabelsAndValue()
    {
        // Whitespace after the label block is optional.
        var ok = PrometheusParser.TryParseLine("m{a=\"1\"}2", out var m);

        Assert.True(ok);
        Assert.Equal(2.0, m!.Value);
    }

    // Не-метрические строки (HELP/TYPE/комментарии/пустые) — false, не исключение

    [Fact]
    public void TryParseLine_NonMetricLines_ReturnFalse()
    {
        Assert.False(PrometheusParser.TryParseLine("# HELP http_requests_total The total number of HTTP requests.", out _));
        Assert.False(PrometheusParser.TryParseLine("# TYPE http_requests_total counter", out _));
        Assert.False(PrometheusParser.TryParseLine("# A comment", out _));
        Assert.False(PrometheusParser.TryParseLine("     ", out _));
        Assert.False(PrometheusParser.TryParseLine("", out _));
        Assert.False(PrometheusParser.TryParseLine(null, out _));
    }

    // Некорректные строки — false, не исключение

    [Fact]
    public void TryParseLine_Malformed_ReturnFalse()
    {
        Assert.False(PrometheusParser.TryParseLine("1bad 1", out _));                 // имя с цифры
        Assert.False(PrometheusParser.TryParseLine("m{9x=\"1\"} 1", out _));           // лейбл с цифры
        Assert.False(PrometheusParser.TryParseLine("m{a=\"1\",a=\"2\"} 1", out _));    // дубликат лейблов
        Assert.False(PrometheusParser.TryParseLine("m{a=\"unterminated", out _));      // незакрытая кавычка
        Assert.False(PrometheusParser.TryParseLine("m{a=\"x\\y\"} 1", out _));         // неизвестный эскейп
        Assert.False(PrometheusParser.TryParseLine("m 1 abc", out _));                 // мусор после значения
        Assert.False(PrometheusParser.TryParseLine("m 1 2.5", out _));                 // не-cелый timestamp
        Assert.False(PrometheusParser.TryParseLine("m 1 99999999999999999999", out _)); // переполнение timestamp (int64)
        Assert.False(PrometheusParser.TryParseLine("m 1 253402300800000", out _));      // timestamp выше DateTimeOffset.MaxValue
        Assert.False(PrometheusParser.TryParseLine("m 1 -62135596800001", out _));     // timestamp ниже DateTimeOffset.MinValue
        Assert.False(PrometheusParser.TryParseLine("m 1.5 2 3", out _));               // два timestamp
        Assert.False(PrometheusParser.TryParseLine("m .", out _));                     // битое значение
    }

    [Fact]
    // DateTimeOffset.MinValue and the last complete millisecond of MaxValue remain valid.
    public void TryParseLine_TimestampAtDateTimeOffsetBoundaries_Accepted()
    {
        Assert.True(PrometheusParser.TryParseLine("m 1 -62135596800000", out var min));
        Assert.Equal(DateTimeOffset.MinValue, min!.Timestamp);

        // MaxValue has sub-millisecond ticks, so verify the upper boundary through milliseconds.
        Assert.True(PrometheusParser.TryParseLine("m 1 253402300799999", out var max));
        Assert.Equal(253402300799999, max!.Timestamp!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    // A colon in a label name must survive the parse-enrich-render pipeline.
    public void TryParseLine_LabelNameWithColon_RoundTripThroughGenerator()
    {
        Assert.True(PrometheusParser.TryParseLine("m{a:b=\"1\"} 1", out var parsed));

        var regenerated = PrometheusParser.MetricLine(parsed!.Name, parsed.Value!.Value, parsed.Labels, _timestamp);

        Assert.Equal("m{a:b=\"1\"} 1 1690000000000", regenerated);
        Assert.True(PrometheusParser.TryParseLine(regenerated, out var m2));
        Assert.Equal(parsed.Labels, m2!.Labels);
    }

    [Fact]
    // The Prometheus specification corpus contains 20 metrics among its 36 lines.
    public void TryParseLine_PrometheusSpecCorpus()
    {
        const string corpus = """
            # HELP http_requests_total The total number of HTTP requests.
            # TYPE http_requests_total counter
            http_requests_total{method="post",code="200"} 1027 1395066363000
            http_requests_total{method="post",code="400"}    3 1395066363000

            # Escaping in label values:
            msdos_file_access_time_seconds{path="C:\\DIR\\FILE.TXT",error="Cannot find file:\n\"FILE.TXT\""} 1.458255915e9

            # Minimalistic line:
            metric_without_timestamp_and_labels 12.47

            # A weird metric from before the epoch:
            something_weird{problem="division by zero"} +Inf -3982045

            # A histogram, which has a pretty complex representation in the text format:
            # HELP http_request_duration_seconds A histogram of the request duration.
            # TYPE http_request_duration_seconds histogram
            http_request_duration_seconds_bucket{le="0.05"} 24054
            http_request_duration_seconds_bucket{le="0.1"} 33444
            http_request_duration_seconds_bucket{le="0.2"} 100392
            http_request_duration_seconds_bucket{le="0.5"} 129389
            http_request_duration_seconds_bucket{le="1"} 133988
            http_request_duration_seconds_bucket{le="+Inf"} 144320
            http_request_duration_seconds_sum 53423
            http_request_duration_seconds_count 144320

            # Finally a summary, which has a complex representation, too:
            # HELP rpc_duration_seconds A summary of the RPC duration.
            # TYPE rpc_duration_seconds summary
            rpc_duration_seconds{quantile="0.01"} 3102
            rpc_duration_seconds{quantile="0.05"} 3272
            rpc_duration_seconds{quantile="0.5"} 4773
            rpc_duration_seconds{quantile="0.9"} 9001
            rpc_duration_seconds{quantile="0.99"} 76656
            rpc_duration_seconds_sum 1.7560473e+07
            rpc_duration_seconds_count 2693
            """;

        var lines = corpus.Split('\n');
        var metricLines = 0;
        foreach (var line in lines)
        {
            if (PrometheusParser.TryParseLine(line, out _))
                metricLines++;
        }

        Assert.Equal(20, metricLines);
    }

    [Fact]
    // Замыкание контура: парсинг -> генерация -> парсинг дают тот же результат.
    // Мутабельные лейблы из распаршенной метрики уходят в MetricLine напрямую.
    public void TryParseLine_RoundTripThroughGenerator()
    {
        var original = "tcp_probe_duration_seconds{instance=\"example.com\",port=\"8080\",note=\"a\\nb\"} 0.0123 1395066363000";
        Assert.True(PrometheusParser.TryParseLine(original, out var m));

        var regenerated = PrometheusParser.MetricLine(m!.Name, m.Value!.Value, m.Labels, m.Timestamp!.Value);

        Assert.True(PrometheusParser.TryParseLine(regenerated, out var m2));

        Assert.Equal(m.Name, m2!.Name);
        Assert.Equal(m.Labels, m2.Labels);
        Assert.Equal(m.Value, m2.Value);
        Assert.Equal(m.Timestamp, m2.Timestamp);
    }

    // Мутабельные лейблы распарсенной метрики: добавление/замена/удаление и ре-эмит

    [Fact]
    public void ParsedMetric_Labels_CanBeAddedReplacedAndRemoved()
    {
        Assert.True(PrometheusParser.TryParseLine("m{a=\"1\",b=\"2\"} 3", out var m));

        m!.Labels["a"] = "10";    // замена
        m.Labels["c"] = "3";     // добавление
        m.Labels.Remove("b");    // удаление

        Assert.Equal(new Dictionary<string, string> { ["a"] = "10", ["c"] = "3" }, m.Labels);

        var line = PrometheusParser.MetricLine(m.Name, m.Value!.Value, m.Labels, _timestamp);
        Assert.Equal("m{a=\"10\",c=\"3\"} 3 1690000000000", line);
    }
}
