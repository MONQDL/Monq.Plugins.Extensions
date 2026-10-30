namespace Monq.Plugins.Extensions.Prometheus;

/// <summary>
/// Represents a metric parsed from the Prometheus text exposition format.
/// </summary>
/// <param name="Name">The metric name.</param>
/// <param name="Labels">
/// The mutable label collection. Labels can be added, replaced, or removed before the metric is
/// rendered again. A <see langword="null"/> value marks a label that is omitted during rendering.
/// </param>
/// <param name="Value">
/// The metric value, or <see langword="null"/> when the source value is <c>Nan</c> or <c>NaN</c>.
/// </param>
/// <param name="Timestamp">
/// The sample timestamp, or <see langword="null"/> when the source line has no timestamp.
/// </param>
public sealed record ParsedMetric(
    string Name,
    Dictionary<string, string?> Labels,
    double? Value,
    DateTimeOffset? Timestamp);
