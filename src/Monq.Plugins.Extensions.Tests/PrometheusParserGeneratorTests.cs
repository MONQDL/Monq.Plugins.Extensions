using Monq.Plugins.Extensions.Prometheus;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public sealed class PrometheusParserGeneratorTests
{
    static readonly DateTimeOffset _timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1690000000000);

    [Fact]
    // Ожидание взято из тестового корпуса Fennel (CSharpApiTests.MetricLine) —
    // якорь байт-сравнимости с прежним выводом.
    public void MetricLine_MatchesFennelOutput()
    {
        var labels = new Dictionary<string, string> { ["method"] = "post", ["code"] = "200" };

        var line = PrometheusParser.MetricLine("http_requests_total", 1027, labels, DateTimeOffset.FromUnixTimeMilliseconds(1395066363000));

        Assert.Equal("http_requests_total{method=\"post\",code=\"200\"} 1027 1395066363000", line);
    }

    [Fact]
    public void MetricLine_WithoutLabels()
    {
        var line = PrometheusParser.MetricLine("metric_without_labels", 12.47, new Dictionary<string, string>(), _timestamp);

        Assert.Equal("metric_without_labels 12.47 1690000000000", line);
    }

    [Fact]
    public void MetricLine_IntegerValue_HasNoDecimalPoint()
    {
        var line = PrometheusParser.MetricLine("tcp_probe_success", 1.0, new Dictionary<string, string>(), _timestamp);

        Assert.Equal("tcp_probe_success 1 1690000000000", line);
    }

    [Fact]
    // Значения лейблов из тестового корпуса Fennel (ParseText: escaping in label values) —
    // проверено, что парсер агента принимает именно такую экранизацию.
    public void MetricLine_EscapesLabelValues()
    {
        var labels = new Dictionary<string, string>
        {
            ["path"] = @"C:\DIR\FILE.TXT",
            ["error"] = "Cannot find file:\n \"FILE.TXT\" ",
        };

        var line = PrometheusParser.MetricLine("msdos_file_access_time_seconds", 1458255915.0, labels, _timestamp);

        Assert.Equal(
            "msdos_file_access_time_seconds{path=\"C:\\\\DIR\\\\FILE.TXT\",error=\"Cannot find file:\\n \\\"FILE.TXT\\\" \"} 1458255915 1690000000000",
            line);
    }

    [Fact]
    public void MetricLine_PreservesLabelOrder()
    {
        var labels = new Dictionary<string, string> { ["instance"] = "example.com", ["port"] = "8080", ["custom"] = "v" };

        var line = PrometheusParser.MetricLine("m", 0.5, labels, _timestamp);

        Assert.Equal("m{instance=\"example.com\",port=\"8080\",custom=\"v\"} 0.5 1690000000000", line);
    }

    [Fact]
    public void MetricLine_Nan_RendersNan()
    {
        var line = PrometheusParser.MetricLine("m", double.NaN, new Dictionary<string, string>(), _timestamp);

        Assert.Equal("m Nan 1690000000000", line);
    }

    [Fact]
    // Формы спецификации: Fennel (pfloat, case-fold) принимает их обратно —
    // круг «скрап -> ре-эмит» замыкается (корпус: something_weird … +Inf)
    public void MetricLine_Infinity_RendersSpecForms()
    {
        Assert.Equal("m +Inf 1690000000000", PrometheusParser.MetricLine("m", double.PositiveInfinity, new Dictionary<string, string>(), _timestamp));
        Assert.Equal("m -Inf 1690000000000", PrometheusParser.MetricLine("m", double.NegativeInfinity, new Dictionary<string, string>(), _timestamp));
    }

    [Fact]
    public void MetricLine_InvalidMetricName_Throws()
    {
        Assert.Throws<ArgumentException>(() => PrometheusParser.MetricLine("1bad", 1.0, new Dictionary<string, string>(), _timestamp));
        Assert.Throws<ArgumentException>(() => PrometheusParser.MetricLine("bad-name", 1.0, new Dictionary<string, string>(), _timestamp));
        Assert.Throws<ArgumentException>(() => PrometheusParser.MetricLine("", 1.0, new Dictionary<string, string>(), _timestamp));
    }

    [Fact]
    public void MetricLine_InvalidLabelName_Throws()
    {
        var labels = new Dictionary<string, string> { ["9x"] = "v" };

        Assert.Throws<ArgumentException>(() => PrometheusParser.MetricLine("m", 1.0, labels, _timestamp));
    }

    [Fact]
    // «:» в имени лейбла: у Fennel метрика и лейбл — один pname, парсер агента
    // принимает «:» в лейблах, генератору паритет с парсером обязателен
    public void MetricLine_LabelNameWithColon_IsAllowed()
    {
        var labels = new Dictionary<string, string> { ["a:b"] = "1" };

        var line = PrometheusParser.MetricLine("m", 1.0, labels, _timestamp);

        Assert.Equal("m{a:b=\"1\"} 1 1690000000000", line);
    }

    [Fact]
    public void EscapeLabelValue_EscapesOnlyContractSequences()
    {
        Assert.Equal("a\\\\b\\\"c`de", PrometheusParser.EscapeLabelValue(@"a\b" + "\"c`de"));
        Assert.Equal("x\\ny\\rz\\ts", PrometheusParser.EscapeLabelValue("x\ny\rz\ts"));
        Assert.Equal("plain", PrometheusParser.EscapeLabelValue("plain"));
    }

    [Fact]
    public void FormatValue_UsesInvariantCulture()
    {
        Assert.Equal("0.5", PrometheusParser.FormatValue(0.5));
        Assert.Equal("1", PrometheusParser.FormatValue(1.0));
        Assert.Equal("1027", PrometheusParser.FormatValue(1027.0));
    }
}
