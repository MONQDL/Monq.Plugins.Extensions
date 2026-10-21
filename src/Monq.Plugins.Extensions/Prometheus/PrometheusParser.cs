using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Monq.Plugins.Prometheus;

// Форматтер и парсер строк метрик в текстовом формате Prometheus (exposition format).
// Самодостаточен, без внешних зависимостей: задуман как общее решение для плагинов —
//   * «генератор»: плагин выбрасывает метрики в артефакт «metrics» (агент парсит
//     эти строки своим парсером Fennel, поэтому генерация ниже — контракт);
//   * «парсер»: плагин скрапит чужой /metrics (ParseText/TryParseLine) и докидывает
//     свои лейблы в распарсенные метрики (ParsedMetric.Labels) — терпимо, без
//     исключений, в стиле, в котором это делали старые плагины поверх Fennel
//     (битые строки прокидываются, а не роняют задачу).
//
// ПАРИТЕТ API С FENNEL (github.com/dburriss/fennel, фасад CSharp.fs) — удобство,
// которым пользовались старые плагины:
//   - ParseText(text) — парсит целый текст /metrics одним вызовом (у Fennel —
//     Prometheus.ParseText). Терпимый: не-метрические (HELP/TYPE/комментарии/
//     пустые) и некорректные строки пропускаются, исключение не бросается.
//     Прогон — один проход по тексту без промежуточных копий строк построчно;
//   - ParsedMetric.Labels — мутабельный словарь (у Fennel — Metric.Labels :
//     IDictionary<string,string>): после разбора лейблы можно добавлять,
//     заменять и удалять, дальше метрика идёт в MetricLine напрямую;
//   - TryParseLine / MetricLine — терпимые аналоги Fennel'овских
//     Prometheus.ParseLine / Prometheus.Metric.
//
// Аллокации на строку метрики намеренно минимальны: значение и timestamp читаются
// из строки напрямую, без нарезки токенов в новые строки; значение лейбла копируется
// ровно один раз — без эскейпов это слайс исходной строки, с эскейпами —
// StringBuilder; без лейблов промежуточные коллекции не создаются, а дубликаты
// имён ловит Dictionary.TryAdd (отдельный HashSet не нужен).
//
// Контракт формата:
//   name{label="value",...} value [timestamp_millis]
//   - имя метрики и лейбла: [a-zA-Z_:][a-zA-Z0-9_:]* (у Fennel и то, и другое —
//     один и тот же pname; парсер агента принимает «:» и в именах лейблов)
//   - значение лейбла: в кавычках, экранированы \ " \n \r \t
//   - значение метрики: float в инвариантной культуре; NaN рендерится как «Nan»,
//     ±Infinity как «+Inf»/«-Inf» — все эти формы парсер агента (Fennel pfloat,
//     case-fold) принимает обратно
//   - timestamp: целые миллисекунды Unix-времени в диапазоне DateTimeOffset
//     (вне диапазона строка пропускается, исключение не бросается)
//
// ОТЛИЧИЯ ПАРСЕРА ОТ FENNEL (по итогам сравнения с github.com/dburriss/fennel).
//
// Безопасная терпимость — Fennel такие строки отбрасывал, и старые скрейперы
// (k8smetricsdataflow / metricsfromstatictargets) теряли эти метрики молча.
// Мы принимаем, и при повторной генерации через MetricLine строка нормализуется,
// поэтому парсер агента (тот же Fennel) никогда не ломается:
//   - NaN: Fennel (pfloat, case-fold) принимает «nan» в любом регистре и возвращает
//     double.NaN; мы принимаем только точные токены «Nan»/«NaN» и отдаём null вместо
//     double.NaN, чтобы плагин подставлял 0, как делали legacy
//     (metric.MetricValue ?? 0);
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
    // Те же классы, что и MetricNameRegex: у Fennel и метрика, и лейбл — один и тот же
    // pname, парсер агента принимает «:» и в именах лейблов (паритет генератора).
    static readonly Regex LabelNameRegex = new("^[a-zA-Z_:][a-zA-Z0-9_:]*$");

    // Границы валидного timestamp (Unix-миллисекунды) — ровно диапазон DateTimeOffset;
    // long влезает и дальше, без проверки FromUnixTimeMilliseconds бросит исключение.
    // Границы вычисляются из BCL, а не захардкожены: если .NET поменяет диапазон
    // дат (сейчас 0001-01-01..9999-12-31), код подстроится сам
    static readonly long MinTimestampMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxTimestampMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

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

        // Формы спецификации; Fennel (pfloat, case-fold) принимает их обратно —
        // круг ре-эмита замыкается (корпус: something_weird … +Inf)
        if (double.IsPositiveInfinity(value))
            return "+Inf";

        if (double.IsNegativeInfinity(value))
            return "-Inf";

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Парсит целый текст (например, ответ /metrics) — все строки метрик одним
    /// вызовом. Терпимый, как <see cref="TryParseLine(string?, out ParsedMetric?)"/>: не-метрические
    /// (HELP/TYPE/комментарии/пустые) и некорректные строки пропускаются,
    /// исключение не бросается.
    /// </summary>
    public static IReadOnlyList<ParsedMetric> ParseText(string? text)
    {
        var metrics = new List<ParsedMetric>();
        if (string.IsNullOrEmpty(text))
            return metrics;

        // Один проход по тексту; строки режутся индексами, без Split и
        // промежуточных строк (TryParseLine принимает span напрямую).
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            if (TryParseLine(text.AsSpan(start, i - start), out var line))
                metrics.Add(line);
            start = i + 1;
        }

        // Последняя строка без завершающего '\n'
        if (start < text.Length && TryParseLine(text.AsSpan(start), out var lastLine))
            metrics.Add(lastLine);

        return metrics;
    }

    /// <summary>
    /// Парсит одну строку метрики. Терпимый: для не-метрических (HELP/TYPE/комментарии/
    /// пустые) и некорректных строк возвращает <c>false</c> вместо броска исключения.
    /// </summary>
    public static bool TryParseLine(string? line, [NotNullWhen(true)] out ParsedMetric? metric)
    {
        metric = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return TryParseLine(line.AsSpan(), out metric);
    }

    static bool TryParseLine(ReadOnlySpan<char> line, [NotNullWhen(true)] out ParsedMetric? metric)
    {
        metric = null;
        int pos = 0;

        // 1) Имя метрики
        SkipWhitespace(line, ref pos);
        if (pos >= line.Length || !IsNameStartChar(line[pos]))
            return false;
        int nameStart = pos;
        while (pos < line.Length && IsNameChar(line[pos]))
            pos++;
        var name = line[nameStart..pos].ToString();
        // ДЫРА (см. заголовок): принимаем имя в 1 символ; Fennel (manyChars2) требует >= 2.

        // 2) Необязательный блок лейблов.
        // Словарь и собирает пары, и отбраковывает дубликаты имён (TryAdd);
        // порядок вставки сохранён, поэтому ре-эмит воспроизводит порядок лейблов.
        Dictionary<string, string>? labels = null;
        SkipWhitespace(line, ref pos);
        if (pos < line.Length && line[pos] == '{')
        {
            if (!TryParseLabels(line, ref pos, ref labels))
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

        metric = new ParsedMetric(name, labels ?? new Dictionary<string, string>(), value, timestamp);
        return true;
    }

    static bool TryParseLabels(ReadOnlySpan<char> line, ref int pos, ref Dictionary<string, string>? labels)
    {
        // pos указывает на '{'
        pos++;

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
            var labelName = line[nameStart..pos].ToString();
            // ДЫРА (см. заголовок): принимаем имя лейбла в 1 символ; Fennel требует >= 2.

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
            if (!TryParseLabelValue(line, ref pos, out string labelValue))
                return false;

            // Дубликаты имён лейблов отвергаем: обёртка Fennel на них падает
            // (dict с дубликатами), а старые плагины такие строки прокидывают.
            labels ??= new Dictionary<string, string>(4);
            if (!labels.TryAdd(labelName, labelValue))
                return false;

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

    static bool TryParseLabelValue(ReadOnlySpan<char> line, ref int pos, out string value)
    {
        value = string.Empty;
        // pos указывает на первый символ после открывающей '"'
        int valueStart = pos;
        int valueEnd = -1;
        bool hasEscape = false;

        while (pos < line.Length)
        {
            char c = line[pos];
            if (c == '\\')
            {
                pos++;
                if (pos >= line.Length)
                    return false; // «висячий» '\' в конце строки
                char next = line[pos];
                if (next != 'n' && next != 'r' && next != 't' && next != '\\' && next != '"')
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

        if (valueEnd < 0) // закрывающая '"' не найдена
        {
            value = string.Empty;
            return false;
        }

        // Копия значения ровно одна: без эскейпов — слайс исходной строки
        // (StringBuilder не нужен); с эскейпами — разэскейп в билдер.
        if (!hasEscape)
        {
            value = line[valueStart..valueEnd].ToString();
            return true;
        }

        var sb = new StringBuilder(valueEnd - valueStart);
        for (int i = valueStart; i < valueEnd; i++)
        {
            char c = line[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            char next = line[++i];
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

        // Токен до следующего пробела (пробел внутри значений здесь невозможен:
        // блок лейблов, если был, уже потреблён выше). Токен не режется в строку —
        // сравнивается и парсится span'ом.
        int tokenStart = pos;
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
            return true; // value остаётся null
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
        {
            value = result;
            return true;
        }

        return false;
    }

    static bool TryParseTimestamp(ReadOnlySpan<char> line, ref int pos, out DateTimeOffset? timestamp)
    {
        timestamp = null;

        int tokenStart = pos;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        if (pos == tokenStart)
            return false;

        var token = line[tokenStart..pos];
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            return false;

        // Проверка диапазона: int64 влезает шире диапазона DateTimeOffset, и
        // FromUnixTimeMilliseconds на нём бросает — такая строка невалидна,
        // а не весь текст (контракт: терпимо, без исключений)
        if (ms < MinTimestampMs || ms > MaxTimestampMs)
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
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == ':';

    static bool IsNameChar(char c) =>
        IsNameStartChar(c) || (c >= '0' && c <= '9');
}
