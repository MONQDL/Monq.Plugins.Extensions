namespace Monq.Plugins.Prometheus;

/// <summary>
/// Результат разбора одной строки метрики в текстовом формате Prometheus.
/// <see cref="Value"/> равно <c>null</c> для «Nan»/«NaN»;
/// <see cref="Timestamp"/> равно <c>null</c>, если в строке нет таймстемпа.
/// </summary>
public sealed record ParsedMetric(
    string Name,
    IReadOnlyList<(string Name, string Value)> Labels,
    double? Value,
    DateTimeOffset? Timestamp);
