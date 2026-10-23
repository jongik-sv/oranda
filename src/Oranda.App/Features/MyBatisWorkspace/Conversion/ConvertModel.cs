using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

// 바꾸기 엔진(ConvertEngine)과 대화 상자(ConvertDialogViewModel)·쓰기(ConvertWriter)가 함께 쓰는 형(TSK-03-05, MB-08).
// 시작 시험 제약(design.md 9장): 이 파일의 형에는 Language 의 enum·구조체 필드를 두지 않는다(Core 형만).

/// <summary>바꿀 SQL 탭 하나: 탭 이름·글·바인드 변수 창 줄. 살아 있는 탭 VM 에서 뽑는다(작업 공간 파일에는 바인드가 없다).</summary>
public sealed record ConvertSourceTab(string Title, string Text, IReadOnlyList<ConvertBind> Binds);

/// <summary>
/// 탭 바인드 변수 창 한 줄(<c>BindVariableRow</c>). 이름은 콜론 없이, 창이 대문자로 둔 그대로다(찾을 때 대소문자 무시).
/// <see cref="Text"/> 가 비면 NULL 이다.
/// </summary>
public sealed record ConvertBind(string Name, BindDataType Type, BindDirection Direction, string Text);

/// <summary>매퍼 문장 종류. <see cref="Callable"/> = <c>&lt;update statementType="CALLABLE"&gt;</c>(PL/SQL 익명 블록).</summary>
public enum ConvertStatementKind
{
    Select,
    Insert,
    Update,
    Delete,
    Callable,
}

/// <summary>확인 열에 보일 알림 종류. 화면 글은 대화 상자가 strings 키로 만든다.</summary>
public enum ConvertNoteKind
{
    /// <summary><c>&amp;x</c>·<c>&amp;&amp;x</c> → <c>${x}</c>. 값 검증이 필요하다(<see cref="ConvertNote.Name"/> = 이름).</summary>
    Substitution,

    /// <summary>위치 바인드 <c>:1</c> → <c>#{p1}</c>(<see cref="ConvertNote.Name"/> = 새 이름).</summary>
    PositionalBind,

    /// <summary>REF CURSOR OUT 바인드는 바꾸기 범위 밖(<see cref="ConvertNote.Name"/> = 이름).</summary>
    RefCursorOut,

    /// <summary><c>&lt;where&gt;&lt;if&gt;</c> 감싸기를 하지 못했다(최상위 OR·집합 연산·DML 등).</summary>
    WhereNotWrapped,

    /// <summary>PL/SQL 익명 블록이라 기본으로 뺐다(종류를 CALLABLE 로 고르면 넣는다).</summary>
    PlSqlBlock,

    /// <summary>select·insert·update·delete·PL/SQL 이 아닌 문장(DDL·CALL·트랜잭션·SQL*Plus 등)이라 바꿀 수 없다.</summary>
    Unsupported,

    /// <summary>서식을 적용하지 못해 원래 글을 그대로 썼다.</summary>
    FormatSkipped,
}

/// <summary>확인 열 알림 하나.</summary>
public sealed record ConvertNote(ConvertNoteKind Kind, string? Name = null);

/// <summary>
/// 분석 결과 = 대화 상자 「바꿀 탭」 표의 한 줄(문장 하나). 한 탭에 문장이 여럿이면 문장마다 줄이다(조정자 답 Q1).
/// </summary>
/// <param name="TabIndex">원래 작업 공간에서 탭 순서(0 기반).</param>
/// <param name="TabTitle">탭 이름.</param>
/// <param name="StatementIndex">탭 안 문장 순서(0 기반).</param>
/// <param name="Sql">원래 문장 글(끝의 <c>;</c>·<c>/</c> 제외, PL/SQL 블록은 <c>END;</c> 포함).</param>
/// <param name="Kind">제안 종류. 바꿀 수 없는 문장이면 null.</param>
/// <param name="IncludeByDefault">처음에 포함 체크가 켜져 있는가(PL/SQL·바꿀 수 없는 문장은 거짓).</param>
/// <param name="SuggestedId">제안 문장 id(이번 줄끼리·기존 매퍼와 겹치지 않게 접미를 붙인 것).</param>
/// <param name="BindCount">서로 다른 바인드 이름 수.</param>
/// <param name="Notes">확인 열 알림(바꾸기 옵션과 무관한 것).</param>
public sealed record ConvertCandidate(
    int TabIndex,
    string TabTitle,
    int StatementIndex,
    string Sql,
    ConvertStatementKind? Kind,
    bool IncludeByDefault,
    string SuggestedId,
    int BindCount,
    IReadOnlyList<ConvertNote> Notes);

/// <summary>「바꾸는 방법」 체크. <see cref="Format"/> 이 null 이면 서식을 적용하지 않는다.</summary>
public sealed record ConvertOptions
{
    /// <summary>타입을 아는 바인드 모두에 jdbcType 을 붙인다. 꺼도 DATE·TIMESTAMP·CHAR 형 바인드에는 붙인다(결과가 원래 탭과 같게).</summary>
    public bool AddJdbcType { get; init; }

    /// <summary>최상위 WHERE 의 바인드 조건을 <c>&lt;where&gt;&lt;if test="x != null"&gt;</c> 로 감싼다(select 만).</summary>
    public bool WrapWhere { get; init; }

    /// <summary><c>&lt;</c>·<c>&gt;</c>·<c>&amp;</c> 가 있는 문장은 CDATA 로 감싼다. 끄면 엔티티로 바꾼다.</summary>
    public bool UseCdata { get; init; } = true;

    /// <summary>탭 바인드 값을 그 문장의 첫 시나리오로 저장한다.</summary>
    public bool SaveScenario { get; init; } = true;

    /// <summary>기본 포매터 프로필 옵션(<c>AppSettings.FormatterProfileId</c>). null = 서식 없이 원래 글.</summary>
    public FormatterOptions? Format { get; init; }
}

/// <summary>
/// 문장 하나를 바꾼 결과. <see cref="ElementText"/> = 매퍼에 넣을 요소 원문(여는 태그 ~ 닫는 태그, 들여쓰기 없음, 줄 끝 <c>\n</c>).
/// <see cref="ScenarioValues"/> = 첫 시나리오 값(파라미터 이름 → 값, 비밀 이름 제외는 저장소가 한다).
/// </summary>
public sealed record ConvertedStatement(
    string Id,
    ConvertStatementKind Kind,
    string ElementText,
    IReadOnlyDictionary<string, MyBatisScenarioValue> ScenarioValues,
    IReadOnlyList<ConvertNote> Notes);
