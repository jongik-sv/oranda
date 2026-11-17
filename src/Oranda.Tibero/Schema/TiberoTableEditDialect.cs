using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 테이블 편집기 Tibero 방언(설계 8·12장, 실측 정본 docs/design/tibero-table-edit-probe.md). 문장 꼴은 Core 빌더(Oracle 꼴)를 그대로 쓰고, 다른 곳만 여기서 낸다.
/// <list type="bullet">
/// <item>이름 한도 128 바이트(Tibero 6 실측 6장), 가상 식 변경 불가(TBR-8081 → DROP + ADD 와 ⚠), DROP 은 휴지통 없이 영구(USE_RECYCLEBIN=NO 기본, 18장).</item>
/// <item>미지원(문법 7001): 신원·숨김·도메인 열(15장), 일반 → 파티션 바꾸기(측정 밖, 보수적으로 막음).</item>
/// <item>서버 이름(제약·인덱스, TE-8): Oracle 규칙(<c>SYS_</c>·<c>BIN$</c>) + Tibero 의 <c>_SYS_…</c>·<c>&lt;계정&gt;_CON&lt;n&gt;</c>(데이터 복사 <c>TiberoRecreateStep.IsServerName</c> 과 같은 꼴, 소유자만 모름).</item>
/// <item>거짓 차이 정규화(8.4, 실측 7장): 형식 이름(VARCHAR↔VARCHAR2, NVARCHAR↔NVARCHAR2, TIMESTAMP↔TIMESTAMP(6), INTEGER·NUMBER(*,0) = NUMBER(38,0), FLOAT = NUMBER,
/// INTERVAL 기본 정밀도), 길이 기준(N 형식은 늘 글자), 기본값(앞뒤 공백·줄바꿈, 글자 NULL = 없음), 식·조건(서버가 붙인 따옴표·대소문자·공백, <c>CONCAT</c> ↔ <c>||</c> 는 같게 보지 않음).</item>
/// </list>
/// 정규화 결과는 비교에만 쓰고 문장에 넣지 않는다.
/// </summary>
[OrandaService(typeof(ITableEditDialect), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoTableEditDialect : ITableEditDialect
{
    /// <summary>미지원 이유 키 앞머리(<c>strings-*.json</c>).</summary>
    internal const string ReasonPrefix = "TableEdit.unsupported.tibero.";

    private static readonly IReadOnlyList<string> Types =
    [
        "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "NUMBER", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "CLOB", "NCLOB", "BLOB", "RAW",
    ];

    /// <summary>Tibero 가 받지 않는 기능과 이유 키(실측 장 번호).</summary>
    private static readonly Dictionary<TableEditFeature, string> Unsupported = new()
    {
        [TableEditFeature.VirtualColumnModify] = ReasonPrefix + "virtualColumnModify", // 1장 TBR-8081
        [TableEditFeature.IdentityColumn] = ReasonPrefix + "identityColumn", // 15장 7001
        [TableEditFeature.InvisibleColumn] = ReasonPrefix + "invisibleColumn", // 15장 7001
        [TableEditFeature.DomainColumn] = ReasonPrefix + "domainColumn", // 15장 7001
        [TableEditFeature.PartitionConversion] = ReasonPrefix + "partitionConversion", // 실측 밖(MODIFY PARTITION BY 없음 추정) — 보수적으로 막음
    };

    /// <summary>해시 파티션 수 바꾸기(ADD·COALESCE) 미지원 이유 키.</summary>
    internal const string HashPartitionChangeKey = ReasonPrefix + "hashPartitionChange";

    /// <summary>이 방언이 내는 이유 키 모두(문자열 파일에 있는지 시험이 본다).</summary>
    internal static IEnumerable<string> ReasonKeys => Unsupported.Values.Append(HashPartitionChangeKey);

    public Dbms Dbms => Dbms.Tibero;

    public IReadOnlyList<string> TypeNames => Types;

    public bool SupportsVirtualColumnModify => false;

    public bool PurgesOnDrop => true;

    /// <summary>Tibero 6 이상 = 128 바이트(실측). 서버를 모르거나 6 미만이면 작은 쪽 30.</summary>
    public int MaxIdentifierBytes(ServerInfo? server) => server is { MajorVersion: >= 6 } ? 128 : 30;

    public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => Unsupported.GetValueOrDefault(feature);

    /// <summary>
    /// 파티션(실측 11·19): 해시 <c>ADD PARTITION</c>(7002)·<c>COALESCE</c>(7001) 와 일반 → 파티션(<c>MODIFY PARTITION BY</c> 7001)은 안 된다.
    /// RANGE/LIST 의 ADD·DROP·SPLIT·MERGE·RENAME·TRUNCATE·SET INTERVAL 은 된다.
    /// </summary>
    public string? UnsupportedPartitionReasonKey(PartitionOperation operation, PartitionMethod method, ServerInfo? server) => operation switch
    {
        PartitionOperation.Convert => Unsupported[TableEditFeature.PartitionConversion],
        PartitionOperation.Add or PartitionOperation.Coalesce when method == PartitionMethod.Hash => HashPartitionChangeKey,
        _ => null,
    };

    /// <summary>나누기·합치기에 <c>UPDATE INDEXES</c> 를 붙여도 전역 인덱스가 UNUSABLE 로 남는다(로컬은 남음, 실측 19) → 빌더가 REBUILD 를 붙인다.</summary>
    public bool KeepsGlobalIndexesOnSplitMerge => false;

    /// <summary><c>MODIFY PARTITION BY … ONLINE</c> 이 없다(실측 19) → 다시 만들기 방식.</summary>
    public bool SupportsOnlinePartitionConversion(ServerInfo? server) => false;

    public bool IsServerName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // 소유자를 모르는 계약이라 <계정>_CON<n> 을 끝 꼴로 본다(숫자 길이 무관). 사용자 이름을 서버 이름으로 잘못 봐도 ⚠ 하나가 더 나올 뿐이다.
        return name.StartsWith("SYS_", StringComparison.Ordinal) || name.StartsWith("_SYS_", StringComparison.Ordinal)
            || name.StartsWith("BIN$", StringComparison.Ordinal) || ServerNamePattern().IsMatch(name);
    }

    public string NormalizeType(ColumnDraft column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var raw = Spaces().Replace((column.DataType ?? string.Empty).Trim().ToUpperInvariant(), " ");
        var (name, inner) = SplitParens(raw);

        switch (name)
        {
            case "VARCHAR" or "VARCHAR2":
                return Chars("VARCHAR2", column, inner, nationalOnly: false);
            case "CHAR" or "CHARACTER":
                return Chars("CHAR", column, inner, nationalOnly: false, defaultLength: 1);
            case "NVARCHAR" or "NVARCHAR2":
                return Chars("NVARCHAR2", column, inner, nationalOnly: true);
            case "NCHAR":
                return Chars("NCHAR", column, inner, nationalOnly: true, defaultLength: 1);
            case "INTEGER" or "INT" or "SMALLINT":
                return "NUMBER(38,0)";
            case "NUMBER" or "NUMERIC" or "DECIMAL":
                return column.Precision is null && column.Scale is null && inner is not null ? NumberText(inner) : Number(column.Precision, column.Scale);
            case "FLOAT":
                // Tibero 는 FLOAT 를 NUMBER 로 만든다(실측 7장). 정밀도 있는 FLOAT(n) 은 사전에서 NUMBER(≈n×0.3) 로 바뀌어 같게 볼 수 없다 — 그대로 둔다.
                return (column.Precision ?? ParseFirst(inner)) is { } bits ? Invariant($"FLOAT({bits})") : "NUMBER";
            case "RAW":
                return Invariant($"RAW({column.Length ?? ParseFirst(inner) ?? 0})");
        }

        // TIMESTAMP[(p)] [WITH [LOCAL] TIME ZONE], INTERVAL …: 소수 초 기본 6, 앞자리 기본 2.
        if (raw.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            // 칸 값(정밀도)이 먼저 — 형식 글 「TIMESTAMP(6)」 은 그대로 두고 정밀도 칸만 고친 편집을 놓치지 않게(NUMBER·문자와 같은 순서).
            var p = column.Precision?.ToString(CultureInfo.InvariantCulture)
                ?? (TimestampPrecision().Match(raw) is { Success: true } m ? m.Groups[1].Value : "6");
            var zone = raw.EndsWith("WITH LOCAL TIME ZONE", StringComparison.Ordinal) ? " WITH LOCAL TIME ZONE"
                : raw.EndsWith("WITH TIME ZONE", StringComparison.Ordinal) ? " WITH TIME ZONE"
                : string.Empty;
            return $"TIMESTAMP({p}){zone}";
        }

        if (raw.StartsWith("INTERVAL YEAR", StringComparison.Ordinal))
        {
            return $"INTERVAL YEAR({Group(IntervalLead().Match(raw), "2")}) TO MONTH";
        }

        if (raw.StartsWith("INTERVAL DAY", StringComparison.Ordinal))
        {
            return $"INTERVAL DAY({Group(IntervalLead().Match(raw), "2")}) TO SECOND({Group(IntervalSecond().Match(raw), "6")})";
        }

        return raw;
    }

    public string? NormalizeDefault(string? serverText)
    {
        var text = serverText?.Trim();
        return string.IsNullOrEmpty(text) || string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    public string? NormalizeExpression(string? serverText) => Canonical(serverText);

    public string? NormalizeCondition(string? serverText) => Canonical(serverText);

    /// <summary>
    /// 식·조건 비교 표기: 문자열 리터럴(<c>'…'</c>) 밖에서 공백을 지우고 대문자로, 일반 식별자를 감싼 큰따옴표를 벗기고, 바깥 괄호를 벗긴다.
    /// 리터럴 안은 그대로. 빈 값은 null. 서버가 우선순위대로 더하는 안쪽 괄호(<c>a*2 + b</c> → <c>((a * 2) + b)</c>)와 <c>||</c> → <c>CONCAT</c> 은
    /// 지우지 않는다 — 괄호를 지우면 묶음만 바꾼 진짜 변경을 놓친다. 고치지 않은 줄은 서버 표기를 그대로 들고 있어 같다(거짓 차이 없음).
    /// </summary>
    internal static string? Canonical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                var end = i + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\'' && end + 1 < text.Length && text[end + 1] == '\'')
                    {
                        end += 2;
                        continue;
                    }

                    if (text[end] == '\'')
                    {
                        break;
                    }

                    end++;
                }

                end = Math.Min(end, text.Length - 1);
                sb.Append(text, i, end - i + 1);
                i = end + 1;
            }
            else if (c == '"')
            {
                // 따옴표 이름: 안의 "" 는 따옴표 하나(이름 안의 글자)라 건너뛴다.
                var end = i + 1;
                while (end < text.Length && (text[end] != '"' || (end + 1 < text.Length && text[end + 1] == '"')))
                {
                    end += text[end] == '"' ? 2 : 1;
                }

                if (end >= text.Length)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }

                var name = text[(i + 1)..end];
                sb.Append(PlainName().IsMatch(name) ? name : text[i..(end + 1)]);
                i = end + 1;
            }
            else if (char.IsWhiteSpace(c))
            {
                // 공백 덩어리는 한 칸으로 두되, 기호 옆이면 지운다(낱말 경계만 남김: X NOT IN ≠ XNOT IN).
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (sb.Length > 0 && i < text.Length && IsWordChar(sb[^1]) && IsWordChar(text[i]))
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(char.ToUpperInvariant(c));
                i++;
            }
        }

        var result = sb.ToString();
        while (result.Length >= 2 && result[0] == '(' && result[^1] == ')' && Wraps(result))
        {
            result = result[1..^1];
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>낱말 글자(식별자·수·따옴표 이름 끝)인가 — 둘 사이 공백만 뜻이 있다.</summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '"' or '\'';

    /// <summary>맨 앞 <c>(</c> 의 짝이 맨 끝 <c>)</c> 인가(리터럴은 이미 정리돼 괄호가 섞여 있을 수 있어 리터럴을 건너뛴다).</summary>
    private static bool Wraps(string s)
    {
        var depth = 0;
        var inLiteral = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'')
            {
                inLiteral = !inLiteral;
                continue;
            }

            if (inLiteral)
            {
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0 && i < s.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    /// <summary>문자 형식: 길이(칸 값, 없으면 괄호 안 첫 수, 없으면 기본) + 길이 기준(N 형식은 늘 글자라 쓰지 않음, 기준을 모르면 쓰지 않음).</summary>
    private static string Chars(string name, ColumnDraft column, string? inner, bool nationalOnly, int? defaultLength = null)
    {
        var length = column.Length ?? ParseFirst(inner) ?? defaultLength;
        if (nationalOnly)
        {
            return length is null ? name : Invariant($"{name}({length})");
        }

        var semantics = column.CharSemantics
            ?? (inner?.Contains("CHAR", StringComparison.Ordinal) == true ? CharSemantics.Characters
                : inner?.Contains("BYTE", StringComparison.Ordinal) == true ? CharSemantics.Bytes
                : null);
        var unit = semantics switch
        {
            CharSemantics.Characters => " CHAR",
            CharSemantics.Bytes => " BYTE",
            _ => string.Empty,
        };
        return length is null ? name : Invariant($"{name}({length}{unit})");
    }

    /// <summary>NUMBER: 정밀도·눈금 없음 = NUMBER, 정밀도 없음 + 눈금 = (38,s)(<c>NUMBER(*,s)</c>), 정밀도 + 눈금 없음 = (p,0).</summary>
    private static string Number(int? precision, int? scale) => (precision, scale) switch
    {
        (null, null) => "NUMBER",
        (null, { } s) => Invariant($"NUMBER(38,{s})"),
        ({ } p, null) => Invariant($"NUMBER({p},0)"),
        ({ } p, { } s) => Invariant($"NUMBER({p},{s})"),
    };

    /// <summary>괄호 안 글(<c>*,0</c>·<c>10,2</c>·<c>10</c>)의 NUMBER 표기.</summary>
    private static string NumberText(string inner)
    {
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        int? precision = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        int? scale = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var sc) ? sc : null;
        return parts[0] == "*" && scale is null ? "NUMBER" : Number(precision, scale);
    }

    private static (string Name, string? Inner) SplitParens(string raw)
    {
        var open = raw.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return (raw, null);
        }

        var close = raw.IndexOf(')', open);
        var tail = close < 0 ? string.Empty : raw[(close + 1)..].Trim();
        var inner = close < 0 ? raw[(open + 1)..] : raw[(open + 1)..close];
        return (tail.Length == 0 ? raw[..open].Trim() : raw, inner);
    }

    private static int? ParseFirst(string? inner)
    {
        if (inner is null)
        {
            return null;
        }

        var m = FirstNumber().Match(inner);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Group(Match m, string fallback) => m.Success ? m.Groups[1].Value : fallback;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@".+_CON[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerNamePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^TIMESTAMP\s*\(\s*([0-9]+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrecision();

    [GeneratedRegex(@"^INTERVAL (?:YEAR|DAY)\s*\(\s*([0-9]+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex IntervalLead();

    [GeneratedRegex(@"SECOND\s*\(\s*([0-9]+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex IntervalSecond();

    [GeneratedRegex(@"[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
