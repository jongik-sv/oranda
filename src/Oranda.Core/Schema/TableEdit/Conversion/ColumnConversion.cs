using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit.Conversion;

/// <summary>열 형식 변경 자동 변환 방법(설계 18.9·시안 ⑧).</summary>
public enum ColumnConversionMethod
{
    /// <summary>열 바꿔치기: 임시 열 추가 → 값 복사(UPDATE) → 옛 열 지우기 → 이름 바꾸기 → 정의 되살리기 → 다시 컴파일. 빠르고 열이 맨 뒤로 간다.</summary>
    ColumnSwap,

    /// <summary>테이블 다시 만들기: 열 순서 바꾸기의 다시 만들기 스크립트(<see cref="ColumnReorderBuilder.Rebuild"/>)에 변환식을 끼운다. 열 순서를 지키고 오래 걸린다. 스크립트만 만든다.</summary>
    Rebuild,
}

/// <summary>
/// 변환 하나의 입력. <see cref="Original"/> = 지금 서버의 테이블(편집을 켤 때의 원본), <see cref="RowKey"/> = 바꿀 열 줄,
/// <see cref="Target"/> = 새 정의(형식·NULL·기본값·주석, 이름이 다르면 마지막에 그 이름이 된다), <see cref="Expression"/> = 변환식(옛 열 이름을 쓴 SQL 식).
/// </summary>
public sealed record ColumnConversionRequest(TableDraft Original, string RowKey, ColumnDraft Target, string Expression)
{
    /// <summary>6단계에서 다시 컴파일할 객체(<see cref="ITableEditChecks.DependentsAsync"/>). 모르면 비움.</summary>
    public IReadOnlyList<DbObjectRef> Dependents { get; init; } = [];

    /// <summary>바꿀 열(원본).</summary>
    public ColumnDraft Source => Original.Column(RowKey) ?? throw new InvalidOperationException($"원본에 열 줄 {RowKey} 가 없다.");

    /// <summary>지금 이름의 테이블.</summary>
    public DbObjectRef Table => new(Original.Owner, Original.Name, "TABLE");
}

/// <summary>열 바꿔치기 단계(설계 18.9 1~6). 값 = 화면의 단계 번호.</summary>
public enum ColumnConversionStepKind
{
    /// <summary>1 <c>ADD (c$N 새 형식)</c>.</summary>
    AddTemporary = 1,

    /// <summary>2 <c>UPDATE … SET c$N = 변환식</c> + <c>COMMIT</c>(⚠ DML, 커밋은 이 단계 끝에 한 번).</summary>
    CopyValues = 2,

    /// <summary>3 <c>DROP COLUMN c CASCADE CONSTRAINTS</c>(⚠ 되돌릴 수 없음).</summary>
    DropOriginal = 3,

    /// <summary>4 <c>RENAME COLUMN c$N TO c</c>.</summary>
    RenameTemporary = 4,

    /// <summary>5 기본값·NOT NULL·주석·이 열의 제약/인덱스 다시 만들기.</summary>
    RestoreDefinition = 5,

    /// <summary>6 이 테이블을 쓰는 뷰·PL/SQL 다시 컴파일.</summary>
    Recompile = 6,
}

/// <summary>단계 하나. 문장이 없는 단계(되살릴 것·다시 컴파일할 것 없음)는 계획에 넣지 않는다.</summary>
public sealed record ColumnConversionStep(ColumnConversionStepKind Kind, IReadOnlyList<SqlStatement> Statements)
{
    public int Number => (int)Kind;

    /// <summary>미리보기·묶음의 ⚠ 표시.</summary>
    public TableEditMarks Marks => Kind switch
    {
        ColumnConversionStepKind.DropOriginal => TableEditMarks.Irreversible,
        ColumnConversionStepKind.RestoreDefinition => TableEditMarks.Recreate,
        _ => TableEditMarks.None,
    };

    /// <summary>DML 단계인가(묶음 항목 종류 「변환 DML」, 의뢰 스크립트 <c>-- DML 포함</c>, TE-44).</summary>
    public bool IsDml => Kind == ColumnConversionStepKind.CopyValues;
}

/// <summary>
/// 열 바꿔치기 계획(문장만, 실행하지 않음 — 실행은 정책 관문으로). <see cref="Statements"/> 를 앞에서부터 하나씩 실행하고,
/// 중간에 실패하면 <see cref="RemainingScript"/> 로 남은 단계를 SQL 편집기에 연다(설계 18.9).
/// </summary>
/// <param name="Column">바꿀 열의 지금 이름</param>
/// <param name="TemporaryColumn">임시 열 이름(<c>&lt;열&gt;$N</c>, TE-49)</param>
/// <param name="FinalColumn">끝난 뒤 이름(<see cref="ColumnConversionRequest.Target"/> 이름)</param>
public sealed record ColumnConversionPlan(
    DbObjectRef Table, string Column, string TemporaryColumn, string FinalColumn, string Expression, IReadOnlyList<ColumnConversionStep> Steps)
{
    /// <summary>모든 문장(실행 순서).</summary>
    public IReadOnlyList<SqlStatement> Statements => [.. Steps.SelectMany(s => s.Statements)];

    /// <summary>문장 위치(0부터) → 그 문장의 단계.</summary>
    public ColumnConversionStep StepOf(int statementIndex)
    {
        var seen = 0;
        foreach (var step in Steps)
        {
            seen += step.Statements.Count;
            if (statementIndex < seen)
            {
                return step;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(statementIndex), statementIndex, "계획의 문장 수를 넘었다.");
    }

    /// <summary>앞에서부터 <paramref name="applied"/> 문장이 성공했을 때 끝난 단계 수(그 단계의 문장이 다 성공한 것만).</summary>
    public int CompletedSteps(int applied)
    {
        var done = 0;
        var seen = 0;
        foreach (var step in Steps)
        {
            seen += step.Statements.Count;
            if (applied < seen)
            {
                break;
            }

            done++;
        }

        return done;
    }

    /// <summary>
    /// 남은 단계 스크립트(설계 18.9 「중간 실패」): 앞에서부터 <paramref name="applied"/> 문장이 성공한 뒤 남은 문장을 SQL 편집기용 글로.
    /// 머리 주석에 테이블·남은 단계, UPDATE 가 남았으면 <c>-- DML 포함</c>. 문장 끝은 <c>;</c>, PL/SQL 은 다음 줄 <c>/</c>. 남은 것이 없으면 빈 문자열.
    /// </summary>
    public string RemainingScript(int applied, string newline = "\n")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(applied);
        var all = Statements;
        if (applied >= all.Count)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var first = StepOf(applied).Number;
        sb.Append("-- 형식 변환 남은 단계: ").Append(Table.Owner).Append('.').Append(Table.Name).Append('.').Append(Column)
            .Append(" (").Append(first).Append("단계부터)").Append(newline);
        if (all.Skip(applied).Any(s => s.Kind == StatementKind.Dml))
        {
            sb.Append(ColumnConversionBuilder.DmlHeader).Append(newline);
        }

        foreach (var statement in all.Skip(applied))
        {
            sb.Append(newline).Append(ColumnConversionBuilder.Terminated(statement, newline)).Append(newline);
        }

        return sb.ToString();
    }
}

/// <summary>
/// 「변환 안 되는 행」 세기 결과(설계 18.9 검사). <see cref="Count"/> ≥ 1 이면 ✖.
/// <see cref="IsLowerBound"/> = 다 세지 못했다(시간 제한 등) — 적어도 <see cref="Count"/> 건.
/// </summary>
public sealed record ConversionFailures(long Count)
{
    public static ConversionFailures None { get; } = new(0);

    public bool IsLowerBound { get; init; }

    /// <summary>처음 본 서버 오류(문구에 붙임).</summary>
    public string? FirstError { get; init; }

    /// <summary>「그 행 보기」 SELECT(소유자 붙인 꼴). 없으면 null.</summary>
    public string? ViewRowsSql { get; init; }
}
