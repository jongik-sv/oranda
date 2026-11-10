using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 새로 만드는 표에 딸려 함께 만드는 객체(SQ2, 조정 결정 78). 사전 점검이 원본 카탈로그로 정하고(대상 소유자 이름으로 바꾼 글), 실행기가 적재 뒤 같은 글을 보낸다.
/// 「없으면 만들기」 표 = 전부, 「다시 만들기」 표 = 함수 기반 인덱스·작업 밖 부모 FK 만(트리거·권한은 대상 쪽 받아 두기·되살리기가 우선한다).
/// 어느 하나를 만들지 못해도 표는 실패가 아니다(알림만, 결과 = 부분 실패).
/// </summary>
/// <param name="Triggers">원본 트리거 생성 문장(대상 소유자로 바꾼 글). 꺼져 있던 트리거는 뒤에 <c>ALTER TRIGGER … DISABLE</c> 이 따른다(<see cref="PreservedDdl.Triggers"/> 와 같은 모양)</param>
/// <param name="TriggerObjects"><paramref name="Triggers"/> 와 같은 자리의 트리거(대상 소유자). 컴파일 오류로 INVALID 이면 실행기가 이것으로 끈다</param>
/// <param name="Grants">원본 테이블에 준 권한 가운데 대상에 받을 사용자·롤이 있는(또는 있을 수 있는) 것의 <c>GRANT</c> 문장</param>
/// <param name="FunctionIndexes">함수 기반 인덱스 <c>CREATE INDEX</c> 문장</param>
/// <param name="OuterForeignKeys">작업 밖 부모(대상에 이미 있는 표)를 참조하는 원본 FK 문장. 부모 없는 행이 있으면 NOVALIDATE 로 바꾸지 않고 뺀다</param>
public sealed record CompanionDdl(
    IReadOnlyList<string> Triggers,
    IReadOnlyList<DisabledObject> TriggerObjects,
    IReadOnlyList<string> Grants,
    IReadOnlyList<string> FunctionIndexes,
    IReadOnlyList<(string Parent, string Ddl, string? Name)> OuterForeignKeys)
{
    public static CompanionDdl Empty { get; } = new([], [], [], [], []);

    /// <summary>만들 것이 하나도 없는가.</summary>
    public bool IsEmpty => Triggers.Count + Grants.Count + FunctionIndexes.Count + OuterForeignKeys.Count == 0;

    /// <summary>미리 보기·실행 순서대로의 문장 전부(함수 기반 인덱스 → 트리거 → 권한 → 작업 밖 FK).</summary>
    public IEnumerable<string> Statements => FunctionIndexes.Concat(Triggers).Concat(Grants).Concat(OuterForeignKeys.Select(f => f.Ddl));
}

/// <summary>딸린 객체 문장 만들기(순수 함수). 원본 소유자 이름(<c>"SRC".</c>)을 대상 소유자로 바꾸는 것은 <see cref="CreateTableDdl"/> 의 기본값 치환과 같다.</summary>
public static partial class CopyCompanions
{
    /// <summary>
    /// 함수 기반 인덱스 하나의 <c>CREATE [UNIQUE] INDEX "TGT"."IDX" ON "TGT"."TBL" (식 [DESC], "열" [DESC], …)</c>. 열 정보가 없으면 null.
    /// 식 안의 원본 소유자 접두는 대상 소유자로 바꾼다.
    /// </summary>
    public static string? FunctionIndex(string targetOwner, string table, IndexInfo index, IReadOnlyList<IndexColumnDetail> columns, string sourceOwner)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            return null;
        }

        var parts = columns.OrderBy(c => c.Position).Select(c =>
            (c.Expression is { } e ? CreateTableDdl.Reowned(e.Trim(), sourceOwner, targetOwner)! : SqlIdentifier.QuoteAlways(c.Column)) + (c.Descending ? " DESC" : string.Empty));
        return new StringBuilder("CREATE ").Append(index.IsUnique ? "UNIQUE " : string.Empty).Append("INDEX ")
            .Append(CopyCommonStatements.Qualified(targetOwner, index.Name)).Append(" ON ").Append(CopyCommonStatements.Qualified(targetOwner, table))
            .Append(" (").Append(string.Join(", ", parts)).Append(')').ToString();
    }

    /// <summary>함수 기반 인덱스인가(<c>INDEX_TYPE</c> 에 FUNCTION-BASED). 비트맵 함수 기반도 포함한다.</summary>
    public static bool IsFunctionIndex(IndexInfo index)
        => index?.IndexType?.Contains("FUNCTION-BASED", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 식이 <paramref name="removed"/> 의 열(교차 복사에서 만들지 않는 열·LOB 로 옮기는 열)을 따옴표 이름으로 쓰는가. 쓰면 대상에 만들 수 없다.
    /// </summary>
    public static string? UsesColumn(IReadOnlyList<IndexColumnDetail> columns, IReadOnlySet<string> removed)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(removed);
        foreach (var c in columns)
        {
            if (c.Expression is null)
            {
                if (removed.Contains(c.Column))
                {
                    return c.Column;
                }

                continue;
            }

            foreach (Match m in QuotedName().Matches(c.Expression))
            {
                var name = m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal);
                if (removed.Contains(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 원본 소유자로 쓰인 문장(트리거·권한)을 대상 소유자로: 따옴표 이름 <c>"SRC".</c> → <c>"TGT".</c>, 그리고 트리거 본문에 사람이 따옴표 없이 쓴 <c>SRC.</c>(대소문자 무시, 낱말 경계)
    /// → <c>"TGT".</c>. 따옴표 없는 것을 그대로 두면 대상 트리거가 오류 없이 원본 스키마의 시퀀스·표를 쓴다. 문자열 리터럴 안은 바꾸지 않는다.
    /// </summary>
    public static string Reowned(string text, string sourceOwner, string targetOwner)
    {
        var quoted = CreateTableDdl.Reowned(text, sourceOwner, targetOwner) ?? text;
        if (string.IsNullOrEmpty(quoted) || string.Equals(sourceOwner, targetOwner, StringComparison.Ordinal))
        {
            return quoted;
        }

        var bare = new Regex(@"(?<![\w$#""])" + Regex.Escape(sourceOwner) + @"\.(?=[A-Za-z_""])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var target = SqlIdentifier.QuoteAlways(targetOwner) + ".";
        var sb = new StringBuilder();
        var at = 0;
        foreach (Match literal in StringLiteral().Matches(quoted))
        {
            sb.Append(bare.Replace(quoted[at..literal.Index], target)).Append(literal.Value);
            at = literal.Index + literal.Length;
        }

        return sb.Append(bare.Replace(quoted[at..], target)).ToString();
    }

    [GeneratedRegex("'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StringLiteral();

    /// <summary><c>GRANT … TO "X"[ WITH GRANT OPTION]</c> 의 받는 쪽(따옴표를 벗긴 이름, PUBLIC 은 그대로). 읽지 못하면 null.</summary>
    public static string? Grantee(string grant)
    {
        var m = GranteePattern().Match(grant ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        var name = m.Groups["q"].Success ? m.Groups["q"].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : m.Groups["p"].Value;
        return name;
    }

    [GeneratedRegex("\"((?:[^\"]|\"\")*)\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedName();

    [GeneratedRegex(" TO (?:\"(?<q>(?:[^\"]|\"\")*)\"|(?<p>PUBLIC))(?: WITH GRANT OPTION)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GranteePattern();
}
