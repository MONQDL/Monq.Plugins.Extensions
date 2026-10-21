namespace Monq.Plugins.Prometheus;

/// <summary>
/// Результат разбора одной строки метрики в текстовом формате Prometheus.
/// <see cref="Value"/> равно <c>null</c> для «Nan»/«NaN»;
/// <see cref="Timestamp"/> равно <c>null</c>, если в строке нет таймстемпа.
/// <see cref="Labels"/> — мутабельный словарь (как <c>Metric.Labels</c> у Fennel):
/// после разбора можно добавлять, заменять и удалять лейблы, а дальше прокидывать
/// метрику в <see cref="PrometheusParser.MetricLine"/> напрямую.
/// </summary>
public sealed record ParsedMetric(
    string Name,
    Dictionary<string, string> Labels,
    double? Value,
    DateTimeOffset? Timestamp);
