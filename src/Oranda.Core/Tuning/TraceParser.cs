using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Tuning;

/// <summary>
/// 10046 트레이스 본문을 TKPROF 식으로 묶는다(TN-08, TSK-06-02). DB 조회 없는 순수 함수.
/// <para>
/// 읽는 줄: <c>PARSING IN CURSOR #n len= dep= ... sqlid=</c> ~ <c>END OF STMT</c>(문장 본문), <c>PARSE/EXEC/FETCH #n:c=,e=,p=,cr=,cu=,r=</c>(호출 통계),
/// <c>WAIT #n: nam='..' ela= </c>(대기). <c>STAT</c>·<c>CLOSE</c>·<c>BINDS</c>·<c>XCTEND</c>·<c>***</c> 같은 줄은 건너뛴다.
/// 11g·19c 형식(커서 번호 길이, plh·tim 유무, 12.2+ 의 CLOSE 형식)은 키=값 순서·유무에 기대지 않아 같이 읽힌다.
/// </para>
/// <para>
/// 같은 문장(sqlid, 없으면 hv, 그것도 없으면 본문)은 커서 번호가 달라도 한 줄로 합친다. 커서 번호는 CLOSE 뒤 다른 문장에 다시 쓰이므로
/// 새 <c>PARSING IN CURSOR</c> 를 만날 때 바꿔 단다. 시작이 잘려 본문 없이 호출만 있는 커서는 「커서 #n」 문장으로 모은다.
/// 끝이 잘린 파일(END OF STMT 없음, 반쯤 쓴 줄)은 읽은 데까지 센다.
/// </para>
/// </summary>
public static partial class TraceParser
{
    /// <summary>트레이스 줄(줄 끝 문자는 있어도 된다)을 요약한다. 문장은 처음 나온 순서, 대기는 합계 경과가 큰 순서.</summary>
    public static TraceSummary Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var state = new State();
        foreach (var raw in lines)
        {
            var line = (raw ?? string.Empty).TrimEnd('\r', '\n');
            state.Feed(line);
        }

        state.FinishText();

        var statements = state.Order.Select(a => a.ToStatement()).ToList();
        var waits = state.Waits
            .Select(kv => new TraceWaitTotal(kv.Key, kv.Value.Count, kv.Value.Elapsed))
            .OrderByDescending(w => w.ElapsedMicros)
            .ThenBy(w => w.Event, StringComparer.Ordinal)
            .ToList();
        return new TraceSummary(statements, waits);
    }

    [GeneratedRegex(@"^PARSING IN CURSOR #(\d+)\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ParsingRegex();

    [GeneratedRegex(@"^(PARSE|EXEC|FETCH|WAIT|STAT|CLOSE|ERROR|BINDS) #(\d+)\s*:?\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"([a-z#]+)=('[^']*'|\S*)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFieldRegex();

    [GeneratedRegex(@"nam='([^']*)'\s+ela=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WaitRegex();

    private sealed class State
    {
        public readonly List<Acc> Order = [];
        public readonly Dictionary<string, (long Count, long Elapsed)> Waits = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Acc> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Acc> _byCursor = new(StringComparer.Ordinal);

        // 본문을 읽는 중인 커서(PARSING IN CURSOR ~ END OF STMT).
        private Header? _pending;
        private readonly StringBuilder _text = new();

        public void Feed(string line)
        {
            if (_pending is not null)
            {
                if (string.Equals(line, "END OF STMT", StringComparison.Ordinal))
                {
                    FinishText();
                    return;
                }

                // 끝 표시 없이 다음 커서·호출이 오면(잘린 파일) 본문을 닫고 그 줄을 다시 읽는다.
                var startsNew = ParsingRegex().IsMatch(line);
                var lengthReached = _pending.Length is { } len && _text.Length >= len;
                if (!startsNew && !(lengthReached && CallRegex().IsMatch(line)))
                {
                    if (_text.Length > 0)
                    {
                        _text.Append('\n');
                    }

                    _text.Append(line);
                    return;
                }

                FinishText();
            }

            if (ParsingRegex().Match(line) is { Success: true } parsing)
            {
                _pending = Header.Read(parsing.Groups[1].Value, parsing.Groups[2].Value);
                _text.Clear();
                return;
            }

            if (CallRegex().Match(line) is not { Success: true } call)
            {
                return;
            }

            var verb = call.Groups[1].Value;
            var cursor = call.Groups[2].Value;
            var rest = call.Groups[3].Value;
            switch (verb)
            {
                case "PARSE":
                case "EXEC":
                case "FETCH":
                    For(cursor).Add(verb, CallFields(rest));
                    break;
                case "WAIT":
                    if (WaitRegex().Match(rest) is { Success: true } w)
                    {
                        var name = w.Groups[1].Value;
                        var ela = ToLong(w.Groups[2].Value);
                        Waits[name] = Waits.TryGetValue(name, out var t) ? (t.Count + 1, t.Elapsed + ela) : (1, ela);
                    }

                    break;
            }
        }

        public void FinishText()
        {
            if (_pending is null)
            {
                return;
            }

            var h = _pending;
            var text = _text.ToString();
            _pending = null;
            _text.Clear();

            var key = h.SqlId is { Length: > 0 } id ? "sqlid:" + id
                : h.HashValue is { Length: > 0 } hv && hv != "0" ? "hv:" + hv
                : "text:" + text;
            if (!_byKey.TryGetValue(key, out var acc))
            {
                acc = new Acc(text, h.SqlId, h.Depth);
                _byKey[key] = acc;
                Order.Add(acc);
            }
            else if (acc.SqlText.Length < text.Length)
            {
                acc.SqlText = text; // 앞에서 잘린 본문이면 더 긴 것을 쓴다.
            }

            _byCursor[h.Cursor] = acc;
        }

        private Acc For(string cursor)
        {
            if (_byCursor.TryGetValue(cursor, out var acc))
            {
                return acc;
            }

            // 트레이스를 켜기 전에 파싱된 커서(본문 없음).
            acc = new Acc(string.Create(CultureInfo.InvariantCulture, $"(커서 #{cursor}: PARSING IN CURSOR 줄 없음)"), null, 0);
            _byKey["cursor:" + cursor] = acc;
            _byCursor[cursor] = acc;
            Order.Add(acc);
            return acc;
        }

        private static Dictionary<string, long> CallFields(string rest)
        {
            var fields = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var part in rest.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    fields[part[..eq]] = ToLong(part[(eq + 1)..]);
                }
            }

            return fields;
        }
    }

    private sealed record Header(string Cursor, int? Length, int Depth, string? SqlId, string? HashValue)
    {
        public static Header Read(string cursor, string rest)
        {
            int? length = null;
            var depth = 0;
            string? sqlId = null, hv = null;
            foreach (Match m in HeaderFieldRegex().Matches(rest))
            {
                var value = m.Groups[2].Value.Trim('\'');
                switch (m.Groups[1].Value)
                {
                    case "len":
                        length = (int)Math.Min(ToLong(value), int.MaxValue);
                        break;
                    case "dep":
                        depth = (int)Math.Min(ToLong(value), int.MaxValue);
                        break;
                    case "sqlid":
                        sqlId = value.Length > 0 ? value : null;
                        break;
                    case "hv":
                        hv = value;
                        break;
                }
            }

            return new Header(cursor, length, depth, sqlId, hv);
        }
    }

    private sealed class Acc(string sqlText, string? sqlId, int depth)
    {
        private long _parse, _exec, _fetch, _cpu, _elapsed, _disk, _query, _current, _rows;

        public string SqlText { get; set; } = sqlText;

        public void Add(string verb, Dictionary<string, long> f)
        {
            switch (verb)
            {
                case "PARSE":
                    _parse++;
                    break;
                case "EXEC":
                    _exec++;
                    break;
                default:
                    _fetch++;
                    break;
            }

            _cpu += Get("c");
            _elapsed += Get("e");
            _disk += Get("p");
            _query += Get("cr");
            _current += Get("cu");
            _rows += Get("r");

            long Get(string name) => f.TryGetValue(name, out var v) ? v : 0;
        }

        public TraceStatement ToStatement() => new(SqlText, sqlId)
        {
            Depth = depth,
            ParseCount = _parse,
            ExecuteCount = _exec,
            FetchCount = _fetch,
            CpuMicros = _cpu,
            ElapsedMicros = _elapsed,
            Disk = _disk,
            Query = _query,
            Current = _current,
            Rows = _rows,
        };
    }

    private static long ToLong(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
}
