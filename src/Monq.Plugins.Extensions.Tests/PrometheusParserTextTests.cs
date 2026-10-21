using Monq.Plugins.Extensions.Prometheus;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public sealed class PrometheusParserTextTests
{
    // Fennel-овский Prometheus.ParseText: целый текст одним вызовом,
    // терпимо — не-метрические и битые строки пропускаются, исключений нет.

    [Fact]
    // Тот же корпус спецификации, что и TryParseLine_PrometheusSpecCorpus:
    // 20 метрик из 36 строк, HELP/TYPE/комментарии/пустые не в счёт.
    public void ParseText_PrometheusSpecCorpus()
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

        var metrics = PrometheusParser.ParseText(corpus);

        Assert.Equal(20, metrics.Count);
        Assert.Equal("http_requests_total", metrics[0].Name);
        Assert.Equal(new Dictionary<string, string> { ["method"] = "post", ["code"] = "200" }, metrics[0].Labels);
        Assert.Equal(1027.0, metrics[0].Value);
    }

    [Fact]
    public void ParseText_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PrometheusParser.ParseText(null));
        Assert.Empty(PrometheusParser.ParseText(""));
        Assert.Empty(PrometheusParser.ParseText("# только комментарии\nи ещё один\n"));
    }

    [Fact]
    public void ParseText_CrlfAndMissingTrailingNewline()
    {
        var metrics = PrometheusParser.ParseText("# HELP m help\r\nm{a=\"1\"} 1 1690000000000\r\nn 2");

        Assert.Equal(2, metrics.Count);
        Assert.Equal("m", metrics[0].Name);
        Assert.Equal(1690000000000, metrics[0].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.Equal("n", metrics[1].Name);
        Assert.Null(metrics[1].Timestamp);
    }

    [Fact]
    public void ParseText_SkipsMalformedLines()
    {
        const string text = """
            m 1 1690000000000
            bad line here
            n{a="unterminated
            o 2.5
            """;

        var metrics = PrometheusParser.ParseText(text);

        Assert.Equal(2, metrics.Count);
        Assert.Equal("m", metrics[0].Name);
        Assert.Equal("o", metrics[1].Name);
    }

    [Fact]
    // Типичный кейс скрейпера: скрап -> докинуть свой лейбл в каждую метрику
    // -> переиздать строки для артефакта.
    public void ParseText_ScrapeAddLabelReEmit_FullFlow()
    {
        const string text = """
            # HELP http_requests_total The total number of HTTP requests.
            # TYPE http_requests_total counter
            http_requests_total{method="post",code="200"} 1027 1395066363000
            http_requests_total{method="post",code="400"} 3 1395066363000
            """;

        var metrics = PrometheusParser.ParseText(text);
        Assert.Equal(2, metrics.Count);

        foreach (var m in metrics)
            m.Labels["shard"] = "1";

        var regenerated = PrometheusParser.MetricLine(
            metrics[0].Name,
            metrics[0].Value!.Value,
            metrics[0].Labels,
            metrics[0].Timestamp!.Value);

        Assert.Equal("http_requests_total{method=\"post\",code=\"200\",shard=\"1\"} 1027 1395066363000", regenerated);

        // Ре-эмитится обратно в тот же формат, порядок исходных лейблов сохранён
        Assert.True(PrometheusParser.TryParseLine(regenerated, out var m2));
        Assert.Equal("1", m2!.Labels["shard"]);
        Assert.Equal("post", m2.Labels["method"]);
    }
}
