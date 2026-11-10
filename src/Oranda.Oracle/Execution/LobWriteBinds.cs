using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 대량 쓰기(배열 INSERT)에서 CLOB·NCLOB·BLOB 값을 바인드하는 방법(PF5). 값 = <see cref="OracleBulkExecutionService.LobWrite"/>,
/// 기본은 <see cref="LobWriteBinds.DefaultMode"/>(환경 변수 <see cref="LobWriteBinds.SwitchVariable"/> = <c>locator</c> 면 옛 동작).
/// </summary>
internal enum OracleLobWrite
{
    /// <summary>
    /// 옛 동작: 열 형식 그대로(<c>OracleDbType.Clob</c>·<c>NClob</c>·<c>Blob</c>) 바인드한다. 값이 string·byte[] 이면 드라이버가 값마다 임시 LOB 을
    /// 만들어 채운 뒤 위치자를 보낸다 — LOB 값 하나마다 서버 왕복(만들기·쓰기)이 붙어, 원격 서버에서 LOB 열 3개 테이블이 85행/초에 그쳤다(PF5 측정).
    /// </summary>
    Locator,

    /// <summary>
    /// INSERT … VALUES 의 LOB 값을 LONG(CLOB·NCLOB)·LONG RAW(BLOB) 바인드로 보낸다. 서버가 넣을 때 LOB 으로 바꾸므로 임시 LOB·값마다 왕복이 없다
    /// (Oracle 「Data Interface for Persistent LOBs」: INSERT·UPDATE 의 LOB 열에는 크기와 개수에 상관없이 LONG 바인드를 넣을 수 있다).
    /// 그대로 못 보내는 값(빈 값·큰 값·문자 집합 밖 글자)이 있는 행은 옛 동작으로 보낸다(<see cref="LobWriteBinds.Plan"/>).
    /// </summary>
    LongBind,
}

/// <summary>배열 INSERT 의 연속된 행 구간 하나와 그 구간의 LOB 바인드 방법(<see cref="LobWriteBinds.Plan"/>).</summary>
/// <param name="Start">첫 행 번호(0부터).</param>
/// <param name="Count">행 수(1 이상).</param>
/// <param name="LongBind">참이면 LOB 열을 LONG·LONG RAW 로, 거짓이면 옛 동작(LOB 형식)으로 바인드한다.</param>
internal readonly record struct LobSegment(int Start, int Count, bool LongBind);

/// <summary>배열 INSERT 에서 바인드할 LOB 열 하나: 행 배열의 칸 번호와 글자 LOB(CLOB·NCLOB)인지(거짓 = BLOB).</summary>
internal readonly record struct LobBindColumn(int Index, bool Text);

/// <summary>대상 DB 문자 집합의 종류(<see cref="LobWriteBinds.CharsetOf"/>): 글자 LOB 값을 LONG 으로 보내도 되는 범위가 다르다(<see cref="LobWriteBinds.TextFits"/>).</summary>
internal enum LongTextCharset
{
    /// <summary>유니코드가 아니거나 모른다: ASCII 값만.</summary>
    Other,

    /// <summary>UTF8(CESU-8): 서로게이트가 없는 값만.</summary>
    Utf8,

    /// <summary>AL32UTF8: 짝 없는 서로게이트가 없는 값.</summary>
    Al32Utf8,
}

/// <summary>LONG 바인드 구간이 서버 오류로 실패했을 때 할 일(<see cref="LobWriteBinds.OnLongBindError"/>).</summary>
internal enum LongBindErrorAction
{
    /// <summary>LONG 거부가 아니다: 옛 동작처럼 실패로 올린다.</summary>
    NotRefusal,

    /// <summary>1행 구간의 거부: 같은 행을 옛 동작으로 바로 다시 보낸다(넣은 행이 없다).</summary>
    ResendInPlace,

    /// <summary>여러 행 구간의 거부: 다시 보내지 않고 실패로 올린다(이미 넣은 행이 있을 수 있다 — 호출한 쪽이 되돌리고 한 행씩 다시 넣는다).</summary>
    Fail,
}

/// <summary>
/// LOB 쓰기 바인드(PF5)의 순수 규칙: 쓸 수 있는 문장 모양, 행마다 LONG 바인드로 보낼 수 있는가, 연속 구간 나누기, LONG 바인드를 서버·드라이버가 거부한 오류.
/// 실행은 <see cref="OracleBulkExecutionService"/> 가 한다.
/// </summary>
internal static partial class LobWriteBinds
{
    /// <summary>바인드 방법을 고르는 환경 변수. <c>locator</c> 면 옛 동작(<see cref="OracleLobWrite.Locator"/>), 그 밖(없음 포함)은 <see cref="OracleLobWrite.LongBind"/>.</summary>
    internal const string SwitchVariable = "ORANDA_LOB_WRITE";

    /// <summary>
    /// LONG 바인드로 보내는 LOB 값 하나의 상한(CLOB·NCLOB 글자, BLOB 바이트). 더 긴 값이 있는 행은 옛 동작으로 보낸다: 큰 값은 옮기는 시간이 임시 LOB 왕복보다
    /// 훨씬 길어 손해가 작고, 드라이버의 LONG 배열 바인드가 아주 큰 값에서 어떻게 버퍼를 잡는지 확인하지 않은 범위로 가지 않는다.
    /// </summary>
    internal const int LongBindMaxLength = 32767;

    /// <summary>
    /// LONG 바인드 구간 하나의 바이트 상한: 「행 수 × LOB 열마다 그 구간에서 가장 긴 값의 바이트 상한을 더한 것」(<see cref="SegmentBytes"/>).
    /// 드라이버가 배열 원소마다 그 열에서 가장 긴 값만큼 버퍼를 잡더라도 구간 하나가 이 양을 넘지 않게 끊는다 — 실제 값 바이트 합보다 늘 크거나 같다
    /// (대량 읽기의 <see cref="OracleBulkExecutionService.LobPrefetchFetchBytes"/> 와 같은 크기).
    /// </summary>
    internal const long LongSegmentBudget = 32L * 1024 * 1024;

    /// <summary>글자 하나의 바이트 상한(LONG 은 DB 문자 집합: AL32UTF8 은 BMP 글자 하나가 많아야 3바이트, 서로게이트 짝은 UTF-16 두 단위에 4바이트).</summary>
    internal const int TextBytesPerChar = 3;

    /// <summary>ORA-24381「배열 DML 오류」: 행 오류를 감싸는 겉 오류(행마다의 오류는 <c>Errors[i]</c>). 그 자체로는 거부가 아니다.</summary>
    internal const int ArrayDmlErrors = 24381;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>프로세스 기본 바인드 방법(시작할 때 <see cref="SwitchVariable"/> 를 한 번 읽는다).</summary>
    public static OracleLobWrite DefaultMode { get; } = ModeOf(Environment.GetEnvironmentVariable(SwitchVariable));

    /// <summary><see cref="SwitchVariable"/> 값 → 바인드 방법. 앞뒤 공백·대소문자는 보지 않는다.</summary>
    internal static OracleLobWrite ModeOf(string? value) =>
        string.Equals(value?.Trim(), "locator", StringComparison.OrdinalIgnoreCase) ? OracleLobWrite.Locator : OracleLobWrite.LongBind;

    public static bool IsLob(OracleDbType type) => type is OracleDbType.Clob or OracleDbType.NClob or OracleDbType.Blob;

    /// <summary>LOB 바인드 형식 → LONG 바인드 형식: BLOB = LONG RAW, CLOB·NCLOB = LONG. LOB 이 아니면 그대로.</summary>
    public static OracleDbType LongTypeOf(OracleDbType type) => type switch
    {
        OracleDbType.Blob => OracleDbType.LongRaw,
        OracleDbType.Clob or OracleDbType.NClob => OracleDbType.Long,
        _ => type,
    };

    /// <summary>
    /// LONG 바인드를 넣어도 되는 문장인가: 첫 낱말 INSERT, <c>VALUES (</c> 가 있고 SELECT·RETURNING 이 없으며(INSERT … SELECT·다중 INSERT·MERGE 의
    /// USING 질의에는 LONG 을 쓸 수 없다), LOB 바인드(<paramref name="lobBindNames"/>, 쌍점 없음)마다 글에 한 번만, 함수에 감싸이지 않은 채
    /// 값 목록의 한 칸(<c>(</c>·<c>,</c> 뒤, <c>,</c>·<c>)</c> 앞)으로 나온다. 아니면 거짓(옛 동작).
    /// </summary>
    public static bool IsPlainInsertValues(string text, IEnumerable<string> lobBindNames)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lobBindNames);
        if (OracleBulkExecutionService.LeadingWords(text).First != "INSERT" || !ValuesList().IsMatch(text) || NoLongWord().IsMatch(text))
        {
            return false;
        }

        // 값 목록의 맨 바깥 칸만 본다: TO_CLOB(:C2)·NVL(:C1, :C2) 처럼 함수 괄호 안의 바인드는 칸이 아니다(원격 #165).
        var items = ValuesItems(text);
        if (items is null)
        {
            return false;
        }

        foreach (var name in lobBindNames)
        {
            var bind = ":" + Regex.Escape(name);
            var all = Regex.Count(text, bind + "(?![A-Za-z0-9_$#])", Options, RegexTimeout);
            var bare = items.Count(i => string.Equals(i, ":" + name, StringComparison.OrdinalIgnoreCase));
            if (all != 1 || bare != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 첫 <c>VALUES (</c> 의 값 목록을 맨 바깥 쉼표로 나눈 칸들(앞뒤 공백 뺌). 괄호 깊이를 세고 작은따옴표 글자는 건너뛴다. 목록이 닫히지 않으면 null.
    /// </summary>
    private static List<string>? ValuesItems(string text)
    {
        var match = ValuesList().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var items = new List<string>();
        var depth = 0;
        var start = match.Index + match.Length;
        var quoted = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                quoted = c != '\'';
                continue;
            }

            switch (c)
            {
                case '\'':
                    quoted = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ')':
                    items.Add(text[start..i].Trim());
                    return items;
                case ',' when depth == 0:
                    items.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// 행들을 연속 구간으로 나눈다(행 순서 그대로 — 넣는 순서가 옛 동작과 같다). 모든 LOB 값이 LONG 바인드로 보낼 수 있는 값(<see cref="FitsLongBind"/>)인
    /// 행끼리, 아닌 행끼리 이어 붙이고, LONG 구간은 바이트 상한(<see cref="SegmentBytes"/> &gt; <see cref="LongSegmentBudget"/>)을 넘기 전에 끊는다. 행이 없으면 빈 목록.
    /// </summary>
    /// <param name="charset">대상 DB 문자 집합의 종류(<see cref="CharsetOf"/>). 글자 LOB 이 없으면 보지 않는다.</param>
    public static List<LobSegment> Plan(IReadOnlyList<object?[]> rows, IReadOnlyList<LobBindColumn> lobColumns, LongTextCharset charset)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(lobColumns);
        var segments = new List<LobSegment>();
        var start = 0;
        var count = 0;
        var longBind = false;
        var widest = new int[lobColumns.Count]; // 지금 구간에서 열마다 가장 긴 값(글자·바이트)
        var wider = new int[lobColumns.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var fits = FitsLongBind(rows[r], lobColumns, charset, out _);
            for (var c = 0; c < lobColumns.Count; c++)
            {
                wider[c] = Math.Max(widest[c], LengthOf(rows[r][lobColumns[c].Index]));
            }

            if (count > 0 && (fits != longBind || (fits && SegmentBytes(count + 1, wider, lobColumns) > LongSegmentBudget)))
            {
                segments.Add(new LobSegment(start, count, longBind));
                start = r;
                count = 0;
                for (var c = 0; c < lobColumns.Count; c++)
                {
                    wider[c] = LengthOf(rows[r][lobColumns[c].Index]);
                }
            }

            if (count == 0)
            {
                longBind = fits;
            }

            count++;
            (widest, wider) = (wider, widest);
        }

        if (count > 0)
        {
            segments.Add(new LobSegment(start, count, longBind));
        }

        return segments;
    }

    /// <summary>
    /// 구간 바이트 상한: 행 수 × Σ(열마다 가장 긴 값 × 바이트 상한 — 글자 <see cref="TextBytesPerChar"/>, BLOB 1). 값 바이트 합과 「원소마다 가장 긴 값만큼」
    /// 잡는 버퍼 모두의 상한이다.
    /// </summary>
    public static long SegmentBytes(int rows, IReadOnlyList<int> widestPerColumn, IReadOnlyList<LobBindColumn> lobColumns)
    {
        ArgumentNullException.ThrowIfNull(widestPerColumn);
        ArgumentNullException.ThrowIfNull(lobColumns);
        long perRow = 0;
        for (var c = 0; c < lobColumns.Count; c++)
        {
            perRow += (long)widestPerColumn[c] * (lobColumns[c].Text ? TextBytesPerChar : 1);
        }

        return rows * Math.Max(perRow, 1);
    }

    private static int LengthOf(object? value) => value switch
    {
        string text => text.Length,
        byte[] bytes => bytes.Length,
        _ => 0,
    };

    /// <summary>
    /// 행 하나의 LOB 값을 모두 LONG 바인드로 보내도 값이 그대로 들어가는가. NULL 은 된다(LONG NULL = NULL).
    /// 빈 값(<c>""</c>·<c>byte[0]</c>)은 안 된다: Oracle 은 길이 0 LONG 을 NULL 로 넣어 빈 LOB(EMPTY_CLOB·EMPTY_BLOB)이 NULL 로 바뀐다.
    /// <see cref="LongBindMaxLength"/> 보다 긴 값, 글자 LOB 열에 string 이 아닌 값·BLOB 열에 byte[] 가 아닌 값, 문자 집합으로 그대로 옮기지 못할 수 있는
    /// 글자(<see cref="TextFits"/>)도 안 된다. <paramref name="width"/> = 그 행에서 가장 긴 LOB 값(글자·바이트).
    /// </summary>
    public static bool FitsLongBind(object?[] row, IReadOnlyList<LobBindColumn> lobColumns, LongTextCharset charset, out int width)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(lobColumns);
        width = 0;
        foreach (var column in lobColumns)
        {
            int length;
            switch (row[column.Index])
            {
                case null or DBNull:
                    continue;
                case string text when column.Text:
                    if (!TextFits(text, charset))
                    {
                        return false;
                    }

                    length = text.Length;
                    break;
                case byte[] bytes when !column.Text:
                    length = bytes.Length;
                    break;
                default:
                    return false;
            }

            if (length is 0 or > LongBindMaxLength)
            {
                return false;
            }

            width = Math.Max(width, length);
        }

        return true;
    }

    /// <summary>
    /// 글자 LOB 값을 LONG 으로 보내도 글자가 그대로인가. LONG 은 DB 문자 집합으로 오가므로(NCLOB 도 그렇다 — 옛 동작은 국가 문자 집합으로 보낸다):
    /// AL32UTF8 = 짝 없는 서로게이트가 없는 값, UTF8(CESU-8 — 보조 평면 글자를 서로게이트 짝마다 3바이트씩 담아 드라이버·서버 변환이 어긋날 수 있다) =
    /// 서로게이트가 하나도 없는 값, 그 밖 = ASCII 만으로 된 값(어느 문자 집합이든 ASCII 는 그대로다).
    /// </summary>
    public static bool TextFits(string text, LongTextCharset charset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        switch (charset)
        {
            case LongTextCharset.Utf8:
                return !span.ContainsAnyInRange('\uD800', '\uDFFF');
            case LongTextCharset.Al32Utf8:
                break;
            default:
                return !span.ContainsAnyExceptInRange('\0', '\u007F');
        }

        var i = span.IndexOfAnyInRange('\uD800', '\uDFFF');
        while (i >= 0)
        {
            if (!char.IsHighSurrogate(span[i]) || i + 1 >= span.Length || !char.IsLowSurrogate(span[i + 1]))
            {
                return false;
            }

            i += 2;
            var next = span[i..].IndexOfAnyInRange('\uD800', '\uDFFF');
            i = next < 0 ? -1 : i + next;
        }

        return true;
    }

    /// <summary>DB 문자 집합 이름(<c>NLS_CHARACTERSET</c>) → 종류: AL32UTF8, UTF8(CESU-8), 그 밖(모름 포함).</summary>
    public static LongTextCharset CharsetOf(string? charset) => charset?.Trim().ToUpperInvariant() switch
    {
        "AL32UTF8" => LongTextCharset.Al32Utf8,
        "UTF8" => LongTextCharset.Utf8,
        _ => LongTextCharset.Other,
    };

    /// <summary>
    /// 서버가 LONG 바인드 모양을 받지 않는다는 오류: ORA-00997(LONG 을 쓸 수 없는 자리), ORA-01460(변환 불가),
    /// ORA-01461(LONG 은 LONG 열에만), ORA-01483(DATE·NUMBER 바인드 길이 — 큰 바인드 뒤에 드라이버가 내는 경우가 알려져 있다),
    /// ORA-03146(TTC 버퍼 길이), ORA-22295(한 문장에 LOB·LONG 열 모두 4000바이트 넘게), ORA-24816(LONG·LOB 바인드 뒤의 늘어난 LONG 아닌 바인드).
    /// 같은 번호가 값 때문에(예: 그 행 값이 열에 맞지 않음) 날 수도 있어, 거부인지는 옛 동작으로 다시 보낸 결과로 확정한다(<see cref="OnLongBindError"/>).
    /// </summary>
    public static bool IsLongBindRefusal(int number) => number is 997 or 1460 or 1461 or 1483 or 3146 or 22295 or 24816;

    /// <summary>
    /// LONG 바인드 구간이 서버 오류로 실패했을 때 할 일(PF5). <paramref name="errorNumbers"/> = 겉 오류 번호와 <c>Errors[i]</c> 번호 모두 —
    /// 배열 DML 오류는 겉이 ORA-24381(<see cref="ArrayDmlErrors"/>)이고 행 오류가 안에 오므로 겉 번호만 보면 거부를 놓친다.
    /// <list type="bullet">
    /// <item>거부 번호(<see cref="IsLongBindRefusal"/>)가 하나도 없으면 <see cref="LongBindErrorAction.NotRefusal"/>: 옛 동작처럼 실패로 올린다.</item>
    /// <item>1행 구간이면 <see cref="LongBindErrorAction.ResendInPlace"/>: 그 한 행이 실패했으니 넣은 행이 없다 — 같은 행을 옛 동작으로 바로 다시 보낸다.</item>
    /// <item>여러 행 구간이면 <see cref="LongBindErrorAction.Fail"/>: 배열 DML 은 행 오류 앞의 행(배치 오류 방식이면 다른 행까지)을 이미 넣었을 수 있어 제자리에서
    /// 다시 보내면 겹쳐 들어간다(키 없는 테이블은 조용히). 실패로 올리면 복사 실행기가 배치 세이브포인트로 되돌리고 한 행씩 다시 넣는데, 그 1행 호출들이
    /// 옛 동작으로 다시 보내 거부를 확정한다. 드라이버가 알려 주는 행별 처리 수(<c>ArrayBindRowsAffected</c>)는 실패 뒤에 채워지는지 확인되지 않아 믿지 않는다.</item>
    /// </list>
    /// </summary>
    /// <param name="driverRejected">드라이버가 보내기 전에 바인드를 거부했다(형식·인자 예외) — 번호 없이 거부로 본다.</param>
    public static LongBindErrorAction OnLongBindError(IEnumerable<int> errorNumbers, int segmentRows, bool driverRejected = false)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        if (!driverRejected && !errorNumbers.Any(IsLongBindRefusal))
        {
            return LongBindErrorAction.NotRefusal;
        }

        return segmentRows == 1 ? LongBindErrorAction.ResendInPlace : LongBindErrorAction.Fail;
    }

    [GeneratedRegex(@"\bVALUES\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValuesList();

    [GeneratedRegex(@"\b(SELECT|RETURNING|RETURN)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoLongWord();
}

/// <summary>LOB 쓰기 바인드(PF5) 집계: 바인드 방법별 행 수와 LONG 바인드를 거부당해 옛 동작으로 다시 보낸 횟수. 측정·시험이 읽는다.</summary>
internal sealed class LobWriteCounters
{
    private long _longRows;
    private long _locatorRows;
    private long _fallbacks;
    private long _refusedRuns;
    private long _remembered;

    /// <summary>LONG·LONG RAW 바인드로 넣은 행(LOB 열이 있는 문장만).</summary>
    public long LongRows => Interlocked.Read(ref _longRows);

    /// <summary>옛 동작(LOB 바인드)으로 넣은 행(LOB 열이 있는 문장만).</summary>
    public long LocatorRows => Interlocked.Read(ref _locatorRows);

    /// <summary>1행 LONG 구간이 거부되어(<see cref="LongBindErrorAction.ResendInPlace"/>·드라이버 거부) 같은 행을 옛 동작으로 다시 보낸 횟수.</summary>
    public long Fallbacks => Interlocked.Read(ref _fallbacks);

    /// <summary>여러 행 LONG 구간이 거부 번호로 실패해 다시 보내지 않고 실패로 올린 횟수(<see cref="LongBindErrorAction.Fail"/>).</summary>
    public long RefusedRuns => Interlocked.Read(ref _refusedRuns);

    /// <summary>옛 동작으로 다시 보낸 것이 성공해 그 접속·문장을 앞으로 옛 동작으로 보내기로 기억한 횟수.</summary>
    public long Remembered => Interlocked.Read(ref _remembered);

    public void AddLong(int rows) => Interlocked.Add(ref _longRows, rows);

    public void AddLocator(int rows) => Interlocked.Add(ref _locatorRows, rows);

    public void AddFallback() => Interlocked.Increment(ref _fallbacks);

    public void AddRefusedRun() => Interlocked.Increment(ref _refusedRuns);

    public void AddRemembered() => Interlocked.Increment(ref _remembered);

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"long={LongRows} locator={LocatorRows} fallbacks={Fallbacks} refusedRuns={RefusedRuns} remembered={Remembered}");
}
