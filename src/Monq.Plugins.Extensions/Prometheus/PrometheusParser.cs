using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Monq.Plugins.Extensions.Prometheus;

// Parser and formatter for the Prometheus text exposition format. The implementation is
// self-contained and supports both producers of metrics artifacts and scrapers that read a
// /metrics response, enrich parsed metrics with custom labels, and render them again.
//
// Supported API:
//   - ParseText parses metric lines in source order and skips HELP, TYPE, comments, blank lines,
//     and malformed lines;
//   - TryParseLine parses one metric line and returns false for unsupported syntax;
//   - ParsedMetric.Labels is mutable so callers can add, replace, or remove labels;
//   - MetricLine validates names and renders a normalized metric line.
//
// Supported line format:
//   name{label="value",...} value [timestamp_millis]
//   - metric name: [a-zA-Z_:][a-zA-Z0-9_:]*;
//   - label name: [a-zA-Z_:][a-zA-Z0-9_:]*;
//   - label value: quoted, with \, ", \n, \r, and \t escapes;
//   - metric value: an invariant-culture floating-point value, +Inf, -Inf, Nan, or NaN;
//   - timestamp: optional Unix time in milliseconds within the DateTimeOffset range.
//
// The parser deliberately accepts forms found in external exporters and normalizes them when
// rendered: empty label blocks, trailing label commas, surrounding whitespace, and both Nan and
// NaN spellings. Nan and NaN are represented by a null Value. MetricLine renders NaN as Nan and
// infinities as +Inf and -Inf.
//
// Parsing avoids temporary strings for numeric values, timestamps, and individual document lines.
// Names and label values are materialized because ParsedMetric retains them. Unescaped label
// values are copied directly; escaped values are decoded with a StringBuilder. Dictionary.TryAdd
// detects duplicate label names without a separate HashSet.
/// <summary>
/// Parses and renders metrics in the Prometheus text exposition format.
/// </summary>
public static partial class PrometheusParser
{
    const int EstimatedMetricSuffixLength = 32;
    const int FormattedDoubleBufferLength = 64;

    [GeneratedRegex("^[a-zA-Z_:][a-zA-Z0-9_:]*$")]
    private static partial Regex MetricNameRegex();

    [GeneratedRegex("^[a-zA-Z_:][a-zA-Z0-9_:]*$")]
    private static partial Regex LabelNameRegex();

    static readonly long _minTimestampMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long _maxTimestampMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// Renders a metric as <c>name{labels} value timestamp_millis</c>.
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="value">The metric value.</param>
    /// <param name="labels">
    /// The labels to render in enumeration order. Labels with a <see langword="null"/> value are
    /// omitted.
    /// </param>
    /// <param name="timestamp">The sample timestamp.</param>
    /// <returns>The rendered metric line.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or a label name is invalid.</exception>
    public static string MetricLine(
        string name,
        double value,
        IReadOnlyDictionary<string, string?> labels,
        DateTimeOffset timestamp)
    {
        if (!MetricNameRegex().IsMatch(name))
            throw new ArgumentException($"Invalid Prometheus metric name: '{name}'.", nameof(name));

        var sb = new StringBuilder(name.Length + EstimatedMetricSuffixLength).Append(name);
        if (labels.Count > 0)
            AppendLabels(sb, labels);

        sb.Append(' ');
        AppendValue(sb, value);
        return sb
            .Append(' ')
            .Append(timestamp.ToUnixTimeMilliseconds())
            .ToString();
    }

    /// <summary>
    /// Parses all valid metric lines from Prometheus text, such as a <c>/metrics</c> response.
    /// </summary>
    /// <param name="text">The text to parse. A <see langword="null"/> or empty value produces an empty result.</param>
    /// <returns>
    /// Parsed metrics in source order. HELP, TYPE, comment, blank, and malformed lines are skipped.
    /// </returns>
    public static IReadOnlyList<ParsedMetric> ParseText(string? text)
    {
        var metrics = new List<ParsedMetric>();
        if (string.IsNullOrEmpty(text))
            return metrics;

        // A single pass over the text: lines are sliced by index without Split or intermediate
        // strings (TryParseLine accepts a span directly).
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            if (TryParseLine(text.AsSpan(start, i - start), out var line))
                metrics.Add(line);
            start = i + 1;
        }

        // The last line may lack a terminating '\n'.
        if (start < text.Length && TryParseLine(text.AsSpan(start), out var lastLine))
            metrics.Add(lastLine);

        return metrics;
    }

    /// <summary>
    /// Attempts to parse one Prometheus metric line.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <param name="metric">
    /// When this method returns <see langword="true"/>, contains the parsed metric; otherwise,
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for a valid metric line; otherwise, <see langword="false"/> for
    /// HELP, TYPE, comment, blank, or malformed input.
    /// </returns>
    public static bool TryParseLine(string? line, [NotNullWhen(true)] out ParsedMetric? metric)
    {
        metric = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return TryParseLine(line.AsSpan(), out metric);
    }

    internal static string EscapeLabelValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        AppendEscapedLabelValue(sb, value);
        return sb.ToString();
    }

    internal static string FormatValue(double value)
        => FormatSpecialValue(value) ?? value.ToString(CultureInfo.InvariantCulture);

    static void AppendLabels(StringBuilder sb, IReadOnlyDictionary<string, string?> labels)
    {
        var first = true;
        foreach (var (key, val) in labels)
        {
            if (val is null)
                continue;

            if (!LabelNameRegex().IsMatch(key))
                throw new ArgumentException($"Invalid Prometheus label name: '{key}'.", nameof(key));

            if (first)
                sb.Append('{');
            else
                sb.Append(',');

            sb.Append(key).Append("=\"");
            AppendEscapedLabelValue(sb, val);
            sb.Append('"');
            first = false;
        }

        if (!first)
            sb.Append('}');
    }

    static void AppendEscapedLabelValue(StringBuilder sb, string value)
    {
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    static void AppendValue(StringBuilder sb, double value)
    {
        var specialValue = FormatSpecialValue(value);
        if (specialValue is not null)
        {
            sb.Append(specialValue);
            return;
        }

        Span<char> buffer = stackalloc char[FormattedDoubleBufferLength];
        if (!value.TryFormat(buffer, out var charsWritten, provider: CultureInfo.InvariantCulture))
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return;
        }

        sb.Append(buffer[..charsWritten]);
    }

    static string? FormatSpecialValue(double value) => value switch
    {
        var number when double.IsNaN(number) => "Nan",
        double.PositiveInfinity => "+Inf",
        double.NegativeInfinity => "-Inf",
        _ => null
    };

    static bool TryParseLine(ReadOnlySpan<char> line, [NotNullWhen(true)] out ParsedMetric? metric)
    {
        metric = null;
        var pos = 0;

        // 1) Metric name
        SkipWhitespace(line, ref pos);
        if (pos >= line.Length || !IsNameStartChar(line[pos]))
            return false;
        var nameStart = pos;
        while (pos < line.Length && IsNameChar(line[pos]))
            pos++;
        var name = line[nameStart..pos].ToString();
        // 2) Optional label block. The dictionary both collects pairs and rejects duplicate
        // names (TryAdd); insertion order is preserved, so re-emitted lines keep label order.
        Dictionary<string, string?>? labels = null;
        SkipWhitespace(line, ref pos);
        if (pos < line.Length && line[pos] == '{')
        {
            if (!TryParseLabels(line, ref pos, ref labels))
                return false;
        }

        // 3) Value (required)
        SkipWhitespace(line, ref pos);
        if (pos >= line.Length || !TryParseValue(line, ref pos, out var value))
            return false;

        // 4) Optional timestamp
        DateTimeOffset? timestamp = null;
        SkipWhitespace(line, ref pos);
        if (pos < line.Length && !TryParseTimestamp(line, ref pos, out timestamp))
            return false;

        // 5) Only whitespace may follow the timestamp
        SkipWhitespace(line, ref pos);
        if (pos != line.Length)
            return false;

        metric = new ParsedMetric(name, labels ?? [], value, timestamp);
        return true;
    }

    static bool TryParseLabels(ReadOnlySpan<char> line, ref int pos, ref Dictionary<string, string?>? labels)
    {
        // pos points at '{'
        pos++;

        while (true)
        {
            SkipWhitespace(line, ref pos);

            // '}' may come right away: an empty block {} or a trailing comma {a="1",}
            if (pos < line.Length && line[pos] == '}')
            {
                pos++;
                return true;
            }

            // Label name
            if (pos >= line.Length || !IsNameStartChar(line[pos]))
                return false;
            var nameStart = pos;
            while (pos < line.Length && IsNameChar(line[pos]))
                pos++;
            var labelName = line[nameStart..pos].ToString();
            // '='
            SkipWhitespace(line, ref pos);
            if (pos >= line.Length || line[pos] != '=')
                return false;
            pos++;

            // '"'
            SkipWhitespace(line, ref pos);
            if (pos >= line.Length || line[pos] != '"')
                return false;
            pos++;

            // Label value up to the closing '"', unescaped
            if (!TryParseLabelValue(line, ref pos, out var labelValue))
                return false;

            // Duplicate label names are ambiguous and therefore rejected.
            labels ??= new Dictionary<string, string?>(4);
            if (!labels.TryAdd(labelName, labelValue))
                return false;

            // After a value: ',' (next label) or '}' (end of the block)
            SkipWhitespace(line, ref pos);
            if (pos >= line.Length)
                return false;
            if (line[pos] == ',')
            {
                pos++;
                continue;
            }
            if (line[pos] == '}')
            {
                pos++;
                return true;
            }
            return false;
        }
    }

    static bool TryParseLabelValue(ReadOnlySpan<char> line, ref int pos, out string value)
    {
        value = string.Empty;
        // pos points at the first character after the opening '"'
        var valueStart = pos;
        var valueEnd = -1;
        var hasEscape = false;

        while (pos < line.Length)
        {
            var c = line[pos];
            if (c == '\\')
            {
                pos++;
                if (pos >= line.Length)
                    return false; // a dangling '\' at the end of the line
                var next = line[pos];
                if (next is not ('n' or 'r' or 't' or '\\' or '"'))
                    return false;
                pos++;
                hasEscape = true;
            }
            else if (c == '"')
            {
                valueEnd = pos;
                pos++;
                break;
            }
            else
            {
                pos++;
            }
        }

        if (valueEnd < 0) // the closing '"' was not found
        {
            value = string.Empty;
            return false;
        }

        // The value is copied exactly once: without escapes it is a slice of the source line
        // (no StringBuilder needed); with escapes it is unescaped into a builder.
        if (!hasEscape)
        {
            value = line[valueStart..valueEnd].ToString();
            return true;
        }

        var sb = new StringBuilder(valueEnd - valueStart);
        for (var i = valueStart; i < valueEnd; i++)
        {
            var c = line[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            var next = line[++i];
            sb.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => '"'
            });
        }

        value = sb.ToString();
        return true;
    }

    static bool TryParseValue(ReadOnlySpan<char> line, ref int pos, out double? value)
    {
        value = null;

        // The token runs to the next space (a space cannot occur inside a value here: the label
        // block, if any, was consumed above). The token is not cut into a string; it is compared
        // and parsed as a span.
        var tokenStart = pos;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        if (pos == tokenStart)
            return false;

        var token = line[tokenStart..pos];
        if (token.SequenceEqual("+Inf"))
        {
            value = double.PositiveInfinity;
            return true;
        }
        if (token.SequenceEqual("-Inf"))
        {
            value = double.NegativeInfinity;
            return true;
        }
        if (token.SequenceEqual("NaN") || token.SequenceEqual("Nan"))
            return true; // value remains null
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            value = result;
            return true;
        }

        return false;
    }

    static bool TryParseTimestamp(ReadOnlySpan<char> line, ref int pos, out DateTimeOffset? timestamp)
    {
        timestamp = null;

        var tokenStart = pos;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        if (pos == tokenStart)
            return false;

        var token = line[tokenStart..pos];
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return false;

        // Int64 covers a wider range than DateTimeOffset. Treat an out-of-range timestamp as an
        // invalid metric line instead of allowing FromUnixTimeMilliseconds to throw.
        if (ms < _minTimestampMs || ms > _maxTimestampMs)
            return false;

        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return true;
    }

    static void SkipWhitespace(ReadOnlySpan<char> line, ref int pos)
    {
        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
            pos++;
    }

    static bool IsNameStartChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or ':';

    static bool IsNameChar(char c) =>
        IsNameStartChar(c) || c is >= '0' and <= '9';
}
