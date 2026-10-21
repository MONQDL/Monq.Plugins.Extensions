using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Monq.Plugins.Prometheus;

// Форматтер и парсер строк метрик в текстовом формате Prometheus (exposition format).
// Самодостаточен, без внешних зависимостей: задуман как общее решение для плагинов —
//   * «генератор»: плагин выбрасывает метрики в артефакт «metrics» (агент парсит
//     эти строки своим парсером Fennel, поэтому генерация ниже — контракт);
//   * «парсер»: плагин скрапит чужой /metrics и докидывает свои лейблы
//     (TryParseLine) — терпимый, без исключений, в стиле, в котором это делали
//     старые плагины поверх Fennel (битые строки прокидываются, а не роняют задачу).
//
// Контракт формата:
//   name{label="value",...} value [timestamp_millis]
//   - имя метрики:  [a-zA-Z_:][a-zA-Z0-9_:]*
//   - имя лейбла:   [a-zA-Z_][a-zA-Z0-9_]*
//   - значение лейбла: в кавычках, экранированы \ " \n \r \t
//   - значение метрики: float в инвариантной культуре; NaN рендерится как «Nan»
//     (единственная форма, которую разбирает парсер агента); ±Infinity не представляются
//   - timestamp: целые миллисекунды Unix-времени
//
// ОТЛИЧИЯ ПАРСЕРА ОТ FENNEL (по итогам сравнения с github.com/dburriss/fennel).
//
// Безопасная терпимость — Fennel такие строки отбрасывал, и старые скрейперы
// (k8smetricsdataflow / metricsfromstatictargets) теряли эти метрики молча.
// Мы принимаем, и при повторной генерации через MetricLine строка нормализуется,
// поэтому парсер агента (тот же Fennel) никогда не ломается:
//   - «NaN» (форма спецификации) -> null-значение (Fennel понимает только «Nan»);
//     при ре-эмитах плагин подставляет 0, как делали legacy (metric.MetricValue ?? 0);
//   - хвостовая запятая в лейблах {a="1",} (Fennel падает; metricsfromstatictargets
//     обходил это хак-заменой ",}" -> "}", которая резала и внутри кавычек —
//     т.е. портила значение лейбла вида "x,}"); при ре-эмите запятая исчезает;
//   - пустой блок лейблов {} (Fennel невалиден — sepBy требует >= 1 пары);
//     при ре-эмите генерируется строка вообще без скобок;
//   - пробельные хвосты / '\r' / пробелы вокруг '=' в лейблах (Fennel: FParsec run
//     требует полного потребления строки, любой хвост = строка невалидна);
//     при ре-эмите всё обрезается.
//
// ИЗВЕСТНАЯ ДЫРА (риск, а не терпимость): имена метрик и лейблов в 1 символ.
//   Fennel (manyChars2) требует >= 2 символа; мы принимаем 1. Для скрейперов,
//   которые строят gRPC TimeSeries напрямую, это безвредно — агент текст не видит.
//   НО если плагин скрапит строку и ПЕРЕИЗДАЁТ её текстом через MetricLine, то
//   парсер агента (Fennel) эту строку отвергнёт, и ParseText в агенте выбросит
//   исключение на ВЕСЬ артефакт. Legacy-плагин на Fennel создать такую строку
//   не мог (его парсер отбрасывал 1-символьные имена ещё на этапе скрапа).
//   Вывод: либо переиздающий плагин сам отфильтровывает имена короче 2 символов,
//   либо парсер/генератор ужесточить до >= 2 символов (точки ниже помечены «ДЫРА»).
/// <summary>
/// Renders and parses metric lines in the Prometheus text (exposition) format.
/// </summary>
public static class PrometheusParser
{
    // ДЫРА (см. заголовок): regex допускают имена в 1 символ — ровно те, которые не примет
    // парсер агента (Fennel manyChars2 требует >= 2). Такую строку ре-эмитить нельзя.
    static readonly Regex MetricNameRegex = new("^[a-zA-Z_:][a-zA-Z0-9_:]*$");
    static readonly Regex LabelNameRegex = new("^[a-zA-Z_][a-zA-Z0-9_]*$");

    /// <summary>
    /// Рендерит строку метрики: name{labels} value timestamp_millis.
    /// </summary>
    public static string MetricLine(
        string name,
        double value,
        IReadOnlyDictionary<string, string> labels,
        DateTimeOffset timestamp)
    {
        if (!MetricNameRegex.IsMatch(name))
            throw new ArgumentException($"Invalid Prometheus metric name: '{name}'.", nameof(name));

        return new StringBuilder()
            .Append(name)
            .Append(labels.Count == 0 ? string.Empty : RenderLabels(labels))
            .Append(' ')
            .Append(FormatValue(value))
            .Append(' ')
            .Append(timestamp.ToUnixTimeMilliseconds())
            .ToString();
    }

    static string RenderLabels(IReadOnlyDictionary<string, string> labels)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, val) in labels)
        {
            if (!LabelNameRegex.IsMatch(key))
                throw new ArgumentException($"Invalid Prometheus label name: '{key}'.", nameof(key));

            if (!first)
                sb.Append(',');

            sb.Append(key).Append("=\"").Append(EscapeLabelValue(val)).Append('"');
            first = false;
        }

        return sb.Append('}').ToString();
    }

    /// <summary>
    /// Экранирует значение лейбла согласно exposition format.
    /// </summary>
    internal static string EscapeLabelValue(string value)
    {
        var sb = new StringBuilder(value.Length);
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

        return sb.ToString();
    }

    /// <summary>
    /// Форматирует значение метрики в инвариантной культуре.
    /// </summary>
    internal static string FormatValue(double value)
    {
        if (double.IsNaN(value))
            return "Nan";

        if (double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
            throw new ArgumentException("Infinity is not a representable Prometheus metric value.", nameof(value));

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Парсит одну строку метрики. Терпимый: для не-метрических (HELP/TYPE/комментарии/
    /// пустые) и некорректных строк возвращает <c>false</c> вместо броска исключения.
    /// </summary>
    public static bool TryParseLine(string? line, out ParsedMetric? metric)
    {
        metric = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        int pos = 0;

        // 1) Имя метрики
        SkipWhitespace(line, ref pos);
        if (pos >= line.Length || !IsNameStartChar(line[pos]))
            return false;
        int nameStart = pos;
        while (pos < line.Length && IsNameChar(line[pos]))
            pos++;
        var name = line[nameStart..pos];
        // ДЫРА (см. заголовок): принимаем имя в 1 символ; Fennel (manyChars2) требует >= 2.

        // 2) Необязательный блок лейблов
        var labels = new List<(string, string)>();
        SkipWhitespace(line, ref pos);
        if (pos < line.Length && line[pos] == '{')
        {
            if (!TryParseLabels(line, ref pos, labels))
                return false;
        }

        // 3) Значение (обязательное)
        SkipWhitespace(line, ref pos);
        if (pos >= line.Length || !TryParseValue(line, ref pos, out double? value))
            return false;

        // 4) Необязательный timestamp
        DateTimeOffset? timestamp = null;
        SkipWhitespace(line, ref pos);
        if (pos < line.Length && !TryParseTimestamp(line, ref pos, out timestamp))
            return false;

        // 5) После таймстемпа — только пробельные символы
        SkipWhitespace(line, ref pos);
        if (pos != line.Length)
            return false;

        metric = new ParsedMetric(name, labels, value, timestamp);
        return true;
    }

    static bool TryParseLabels(string line, ref int pos, List<(string, string)> labels)
    {
        // pos указывает на '{'
        pos++;

        // Дубликаты имён лейблов отвергаем: обёртка Fennel на них падает
        // (dict с дубликатами), а старые плагины такие строки прокидывают.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            SkipWhitespace(line, ref pos);

            // К закрывающей '}' можно подойти сразу: пустые {} и хвостовая запятая {a="1",}
            if (pos < line.Length && line[pos] == '}')
            {
                pos++;
                return true;
            }

            // Имя лейбла
            if (pos >= line.Length || !IsNameStartChar(line[pos]))
                return false;
            int nameStart = pos;
            while (pos < line.Length && IsNameChar(line[pos]))
                pos++;
            var labelName = line[nameStart..pos];
            // ДЫРА (см. заголовок): принимаем имя лейбла в 1 символ; Fennel требует >= 2.
            if (!seen.Add(labelName))
                return false;

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

            // Значение лейбла до закрывающей '"', с разэскейпингом
            var sb = new StringBuilder();
            bool closed = false;
            while (pos < line.Length)
            {
                char c = line[pos];
                if (c == '\\' && pos + 1 < line.Length)
                {
                    char next = line[pos + 1];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); pos += 2; break;
                        case 'r': sb.Append('\r'); pos += 2; break;
                        case 't': sb.Append('\t'); pos += 2; break;
                        case '\\': sb.Append('\\'); pos += 2; break;
                        case '"': sb.Append('"'); pos += 2; break;
                        default: return false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    pos++;
                    closed = true;
                    break;
                }
                sb.Append(c);
                pos++;
            }
            if (!closed)
                return false;

            labels.Add((labelName, sb.ToString()));

            // После значения: ',' (следующий лейбл) или '}' (конец блока)
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

    static bool TryParseValue(string line, ref int pos, out double? value)
    {
        value = null;

        // Токен до следующего пробела (пробел внутри значений здесь невозможен:
        // блок лейблов, если был, уже потреблён выше)
        int tokenStart = pos;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        if (pos == tokenStart)
            return false;
        var token = line[tokenStart..pos];

        switch (token)
        {
            case "+Inf": value = double.PositiveInfinity; return true;
            case "-Inf": value = double.NegativeInfinity; return true;
            case "NaN" or "Nan": value = null; return true;
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            value = result;
            return true;
        }

        return false;
    }

    static bool TryParseTimestamp(string line, ref int pos, out DateTimeOffset? timestamp)
    {
        timestamp = null;

        int tokenStart = pos;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        if (pos == tokenStart)
            return false;
        var token = line[tokenStart..pos];

        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return false;

        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return true;
    }

    static void SkipWhitespace(string line, ref int pos)
    {
        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
            pos++;
    }

    static bool IsNameStartChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == ':';

    static bool IsNameChar(char c) =>
        IsNameStartChar(c) || (c >= '0' && c <= '9');
}
