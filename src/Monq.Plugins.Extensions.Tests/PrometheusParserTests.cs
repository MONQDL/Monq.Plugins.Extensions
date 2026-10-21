using Monq.Plugins.Prometheus;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public sealed class PrometheusParserTests
{
    static readonly DateTimeOffset Ts = DateTimeOffset.FromUnixTimeMilliseconds(1690000000000);

    // Векторы из тестового корпуса Fennel (CSharpApiTests / ParserTests) — якорь
    // паритетности: всё, что Fennel парсит, мы парсим в тот же результат.

    [Fact]
    public void TryParseLine_FullLine()
    {
        var ok = PrometheusParser.TryParseLine("http_requests_total{method=\"post\",code=\"200\"} 1027 1395066363000", out var m);

        Assert.True(ok);
        Assert.Equal("http_requests_total", m!.Name);
        Assert.Equal(new[] { ("method", "post"), ("code", "200") }, m.Labels);
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
        // Строка из корпуса Prometheus (и ParserTests.fs): Fennel парсит её pfloat'ом
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
    // Экранированные значения из корпуса Fennel (escaping in label values)
    public void TryParseLine_EscapesLabelValues()
    {
        var ok = PrometheusParser.TryParseLine(
            "msdos_file_access_time_seconds{path=\"C:\\\\DIR\\\\FILE.TXT\",error=\"Cannot find file:\\n \\\"FILE.TXT\\\" \"} 1.458255915e9",
            out var m);

        Assert.True(ok);
        Assert.Equal(new[] { ("path", @"C:\DIR\FILE.TXT"), ("error", "Cannot find file:\n \"FILE.TXT\" ") }, m!.Labels);
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
        Assert.Equal(new[] { ("le", "+Inf") }, m!.Labels);
        Assert.Equal(144320.0, m.Value);
    }

    [Fact]
    public void TryParseLine_NanLiteral()
    {
        var ok = PrometheusParser.TryParseLine("metric_with_nan Nan", out var m);

        Assert.True(ok);
        Assert.Null(m!.Value);
    }

    // Терпимость сверх Fennel (он такие строки отбрасывает — старые плагины теряли их молча)

    [Fact]
    public void TryParseLine_NaNIsAccepted()
    {
        // Fennel понимает только «Nan»; спецификация Prometheus — «NaN»
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
        Assert.Equal(new[] { ("a", "1") }, m!.Labels);
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
        // Квирк Fennel: после '}' пробел не обязателен
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
        Assert.False(PrometheusParser.TryParseLine("m 1 99999999999999999999", out _)); // переполнение timestamp
        Assert.False(PrometheusParser.TryParseLine("m 1.5 2 3", out _));               // два timestamp
        Assert.False(PrometheusParser.TryParseLine("m .", out _));                     // битое значение
    }

    [Fact]
    // Полный корпус спецификации Prometheus (из ParserTests.fs): 20 метрик из 36 строк —
    // ровно столько же, сколько подтверждает тест Fennel.
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
    public void TryParseLine_RoundTripThroughGenerator()
    {
        var original = "tcp_probe_duration_seconds{instance=\"example.com\",port=\"8080\",note=\"a\\nb\"} 0.0123 1395066363000";
        Assert.True(PrometheusParser.TryParseLine(original, out var m));

        var regenerated = PrometheusParser.MetricLine(
            m!.Name,
            m.Value!.Value,
            m.Labels.ToDictionary(l => l.Name, l => l.Value),
            m.Timestamp!.Value);

        Assert.True(PrometheusParser.TryParseLine(regenerated, out var m2));

        // record == сравнивает Labels (IReadOnlyList) по ссылке, поэтому — по компонентам
        Assert.Equal(m.Name, m2!.Name);
        Assert.Equal(m.Labels, m2.Labels);
        Assert.Equal(m.Value, m2.Value);
        Assert.Equal(m.Timestamp, m2.Timestamp);
    }
}
