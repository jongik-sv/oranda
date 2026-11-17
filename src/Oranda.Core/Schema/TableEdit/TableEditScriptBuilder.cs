using Oranda.Core.Execution;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>문장에 붙는 표시(미리보기·묶음의 ⚠, 검사 계획 입력).</summary>
[Flags]
public enum TableEditMarks
{
    None = 0,

    /// <summary>서버 이름(SYS_C…) 제약을 그 이름으로 다룬다 — 다른 DB 에서 실패(TE-8).</summary>
    ServerName = 1,

    /// <summary>되돌릴 수 없는 DROP(열·제약·인덱스).</summary>
    Irreversible = 2,

    /// <summary>바꾸기를 위해 지우고 다시 만드는 짝의 한쪽(제약·인덱스 정의 변경, 가상 식 DROP + ADD).</summary>
    Recreate = 4,
}

/// <summary>
/// 문장 하나와 그 출처. <see cref="Statement"/> 의 <see cref="SqlStatement.Text"/> 가 미리보기에 보이고 그대로 실행·저장된다(설계 4장).
/// </summary>
/// <param name="RowKey">나온 편집 줄(<see cref="TableDraft.TableRowKey"/> = 테이블)</param>
public sealed record TableEditStatement(SqlStatement Statement, string RowKey, TableEditPage Page, TableEditMarks Marks);

/// <summary>편집 하나의 문장 목록(실행 순서, 설계 8.3).</summary>
public sealed record TableEditScript(IReadOnlyList<TableEditStatement> Statements)
{
    public static TableEditScript Empty { get; } = new([]);

    public bool IsEmpty => Statements.Count == 0;

    /// <summary>실행 엔진에 보낼 문장(같은 객체).</summary>
    public IReadOnlyList<SqlStatement> SqlStatements => [.. Statements.Select(s => s.Statement)];

    /// <summary>미리보기 글: 문장마다 끝에 <c>;</c>, 문장 사이 <paramref name="newline"/>.</summary>
    public string PreviewText(string newline = "\n") =>
        string.Join(newline, Statements.Select(s => s.Statement.Text + ";"));
}

/// <summary>
/// 차이 → 문장(설계 8.2 문장 꼴, 8.3 순서, TE-11 한 열 MODIFY 하나). 이름은 늘 소유자를 붙이고, 따옴표는 대문자 규칙을 벗어나거나 예약어일 때만
/// (<c>SqlIdentifier.Quote</c> + <see cref="ITableEditDialect.IsReservedWord"/>). 문장은 모두 <see cref="StatementKind.Ddl"/>.
/// </summary>
public static class TableEditScriptBuilder
{
    /// <summary>원본과 편집의 문장. 바뀐 것이 없으면 <see cref="TableEditScript.Empty"/>.</summary>
    public static TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect) =>
        throw new NotImplementedException("T1b");
}
