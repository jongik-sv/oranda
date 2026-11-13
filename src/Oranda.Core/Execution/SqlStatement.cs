using Oranda.Core.Journal;
using Oranda.Core.Text;

namespace Oranda.Core.Execution;

/// <summary>
/// 문장 분류(TRD 4.3). 이름은 이력 <c>statement_kind</c> 열 값과 같다(data-model 3.2).
/// 분류에 실패하면 정책 관문은 가장 엄격한 규칙(DML)을 적용한다.
/// </summary>
public enum StatementKind
{
    Query,
    Dml,
    Ddl,
    PlSql,
    TransactionControl,
    SqlPlus,
    Other,
}

/// <summary>실행 단위 문장 1개. 언어 서비스가 만들고 실행 엔진·정책 관문이 쓴다.</summary>
/// <param name="Text">서버로 보낼 본문(끝의 <c>;</c>·<c>/</c> 제외, 치환 변수 치환 전)</param>
/// <param name="Range">원본 문서 안의 범위(문서 없이 만든 문장은 <c>default</c>)</param>
/// <param name="Kind">분류</param>
public sealed record SqlStatement(string Text, TextRange Range, StatementKind Kind)
{
    /// <summary>첫 명령어(대문자, 예: <c>SELECT</c>, <c>DROP</c>, <c>ALTER SYSTEM</c>). 모르면 null.</summary>
    public string? Verb { get; init; }

    /// <summary>문장에 나온 바인드 이름(<c>:</c> 제외, 등장 순서, 중복 없음).</summary>
    public IReadOnlyList<string> BindNames { get; init; } = [];

    /// <summary>문장에 나온 치환 변수 이름(앞의 앰퍼샌드 제외).</summary>
    public IReadOnlyList<string> SubstitutionNames { get; init; } = [];

    /// <summary>WHERE 없는 UPDATE/DELETE(SAFE-03 경고 대상).</summary>
    public bool IsUnfilteredDml { get; init; }

    /// <summary>DROP·TRUNCATE·ALTER SYSTEM 같은 위험 명령의 대상 이름(이름 재입력 확인용). 없으면 null.</summary>
    public string? DangerousTarget { get; init; }

    /// <summary>ALTER·DROP·CREATE OR REPLACE·RENAME 의 대상 객체(변경 일지가 실행 전에 이전 DDL 을 보관). 대상이 아니면 null.</summary>
    public ChangeTarget? ChangeTarget { get; init; }

    /// <summary>언어 서비스가 분류에 확신이 없으면 true(정책 관문이 보수적으로 판단).</summary>
    public bool IsClassificationUncertain { get; init; }

    /// <summary>
    /// 문장 글이 문서에서 떨어진 조각들로 이루어진 경우(열 블록 실행)의 대응. 일반 문장은 null 이고 문장 글이 <see cref="Range"/> 와 같다.
    /// 문장을 <c>with</c> 로 복사해도(치환 변수 치환 등) 대응이 함께 따라간다.
    /// </summary>
    public IReadOnlyList<StatementPiece>? SourcePieces { get; init; }

    /// <summary>
    /// 쓰기 문장의 모양(되돌리기 지점 설계 13.3). <c>RowDmlBuilder</c> 처럼 모양을 이미 아는 만든 쪽이 채운다. 모르면 null.
    /// 공급자는 치환 변수를 처리한 문장이면 이 값을 버리고 다시 분석한다.
    /// </summary>
    public Oranda.Core.Rollback.DmlShape? Dml { get; init; }
}

/// <summary>문장 글 안의 한 조각: 문장 글의 <paramref name="TextOffset"/> 부터 <paramref name="Length"/> 글자가 문서의 <paramref name="DocumentOffset"/> 에 있다.</summary>
public readonly record struct StatementPiece(int TextOffset, int DocumentOffset, int Length);
