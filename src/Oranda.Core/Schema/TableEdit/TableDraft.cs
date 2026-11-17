using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 테이블 편집 모델(테이블 편집기 설계 6.3·8.1, TE-3). 편집을 켤 때 <see cref="TableDraftFactory.FromTable"/> 로 원본을 만들고,
/// 화면은 그 사본(편집 모델)을 <c>with</c> 로 바꿔 가며 쥔다. 미리보기·실행·저장은 매번 두 모델의 차이(<see cref="TableDraftDiff"/>)에서
/// 문장(<see cref="TableEditScriptBuilder"/>)을 다시 만든다. 불변 모델이다.
/// </summary>
/// <param name="Owner">소유자(따옴표 없는 사전 표기)</param>
/// <param name="Name">테이블 이름. 원본과 다르면 테이블 이름 바꾸기(설계 18.1). 새 테이블은 사용자가 쓴 이름(비어 있을 수 있다)</param>
public sealed record TableDraft(string Owner, string Name)
{
    /// <summary>테이블 자체(주석·이름·저장 속성)에서 나온 문장의 줄 키(<see cref="TableEditStatement.RowKey"/>).</summary>
    public const string TableRowKey = "$table";

    /// <summary>아직 없는 테이블(새 테이블 탭). 원본은 <see cref="TableDraftFactory.Empty"/>, 차이는 <c>CREATE TABLE</c> 하나로 모인다(설계 7.5).</summary>
    public bool IsNewTable { get; init; }

    /// <summary>테이블 주석. null 과 빈 문자열은 같게 본다(설계 8.4).</summary>
    public string? Comment { get; init; }

    /// <summary>열(화면 순서 = 위치 순서). 지울 열은 목록에 남기고 <see cref="ColumnDraft.IsDropped"/> 로 표시한다(빨강 줄긋기·되살리기, 설계 7.2).</summary>
    public IReadOnlyList<ColumnDraft> Columns { get; init; } = [];

    /// <summary>제약(PK·UK·FK·CHECK). 서버 이름의 NOT NULL CHECK 는 넣지 않는다(열의 NULL 칸이 맡음, 설계 7.3).</summary>
    public IReadOnlyList<ConstraintDraft> Constraints { get; init; } = [];

    /// <summary>인덱스. PK·UK 가 만든 인덱스는 <see cref="IndexDraft.ConstraintName"/> 이 있고 고치지 않는다(설계 7.4).</summary>
    public IReadOnlyList<IndexDraft> Indexes { get; init; } = [];

    /// <summary>테이블 종류(TE-29). 일반이 아니면 열 주석·기본값만 고친다(설계 3장). 새 테이블은 18.10 의 생성 종류.</summary>
    public TableKind Kind { get; init; }

    /// <summary>파티션 테이블인가. 파티션 정의는 1차에서 건드리지 않는다(설계 3장).</summary>
    public bool IsPartitioned { get; init; }

    /// <summary>
    /// 테이블스페이스. 새 테이블은 사용자가 칸을 열어 쓴 것만 문장에 넣는다(TE-22). 원본은 읽은 값. 기존 테이블에서 다른 값을 쓰면 「저장」 페이지의 MOVE(설계 18.2),
    /// 비우면(null·공백) 그대로 둔다.
    /// </summary>
    public string? Tablespace { get; init; }

    /// <summary>
    /// 저장 속성(「저장」 페이지, 설계 18.2). 원본 = 읽은 값(모르면 null 칸). 편집 모델 = 「바꿀 값」: null 칸은 그대로 두고, 원본과 같은 값도 문장이 없다.
    /// 화면은 원본과 같은 값을 「바꿀 값」 칸에 비워 보인다.
    /// </summary>
    public TableStorage Storage { get; init; } = new();

    /// <summary>
    /// 아직 REBUILD 하지 않은 인덱스 이름(지금 서버 이름). MOVE 는 성공하고 뒤 REBUILD 가 멈춘 뒤 <see cref="TableDraftRebase.Rebase"/> 가 채운다 —
    /// 다시 읽은 원본에는 MOVE 할 것이 없어 차이만으로는 남은 REBUILD 를 알 수 없다(설계 18.2).
    /// </summary>
    public IReadOnlyList<string> RebuildIndexes { get; init; } = [];

    /// <summary>
    /// 테이블 이름을 바꿀 때 규칙 이름(<c>PK_&lt;옛 이름&gt;</c>·<c>IX_&lt;옛 이름&gt;_NN</c> 꼴) 제약·인덱스도 따라 바꾸나(설계 18.1 「규칙 이름도 바꾸기」, 기본 끔, TE-38).
    /// </summary>
    public bool RenameRuleNames { get; init; }

    /// <summary>새 전역 임시 테이블의 <c>ON COMMIT PRESERVE ROWS</c>(거짓 = DELETE ROWS, 설계 18.10).</summary>
    public bool OnCommitPreserveRows { get; init; }

    /// <summary>줄 키로 열 찾기. 없으면 null.</summary>
    public ColumnDraft? Column(string rowKey) => Columns.FirstOrDefault(c => c.RowKey == rowKey);

    /// <summary>줄 키로 제약 찾기. 없으면 null.</summary>
    public ConstraintDraft? Constraint(string rowKey) => Constraints.FirstOrDefault(c => c.RowKey == rowKey);

    /// <summary>줄 키로 인덱스 찾기. 없으면 null.</summary>
    public IndexDraft? Index(string rowKey) => Indexes.FirstOrDefault(i => i.RowKey == rowKey);
}

/// <summary>
/// 열 한 줄. 줄 키(<see cref="RowKey"/>)는 편집을 켤 때 붙인 고유 키라 이름을 바꿔도 그대로다(설계 8.1: 이름 바꾸기와 지우고 새로 넣기를 가른다).
/// 새 줄은 <see cref="DraftKeys.New"/> 로 키를 받는다.
/// </summary>
public sealed record ColumnDraft(string RowKey, string Name)
{
    /// <summary>원본의 위치(상세 「#」 칸, 1부터). 새 열은 null.</summary>
    public int? OriginalPosition { get; init; }

    /// <summary>
    /// 형식 이름(<c>VARCHAR2</c>, <c>NUMBER</c>, <c>TIMESTAMP(6)</c> …). 길이·정밀도·소수는 <see cref="Length"/>·<see cref="Precision"/>·<see cref="Scale"/> 에 따로 둔다 —
    /// 화면은 <c>VARCHAR2(100)</c> 처럼 괄호를 붙여 넣지 않는다(괄호가 있으면 그대로 써서 길이 기준이 빠진다). 괄호는 서버 사전이 형식 이름에 붙여 주는 것
    /// (<c>TIMESTAMP(6)</c>·<c>INTERVAL DAY(2) TO SECOND(6)</c>)만 남는다. 가상 열은 비워도 된다(서버가 정함).
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>문자·RAW 형식 길이(글자 또는 바이트, <see cref="CharSemantics"/>).</summary>
    public int? Length { get; init; }

    /// <summary>길이 기준. 새 열은 null(세션 기본값, TE-6). 기존 열은 원래 기준을 유지한다.</summary>
    public CharSemantics? CharSemantics { get; init; }

    /// <summary>NUMBER·FLOAT 정밀도. null 이면 쓰지 않음(<c>NUMBER</c>, 소수만 0 이면 <c>NUMBER(*,0)</c>).</summary>
    public int? Precision { get; init; }

    /// <summary>NUMBER 소수 자리.</summary>
    public int? Scale { get; init; }

    public bool IsNullable { get; init; } = true;

    /// <summary>기본값 식(서버 표기 그대로 또는 사용자가 쓴 식). null·공백 = 없음.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>가상 식. 값이 있으면 가상 열이다(TE-30: 공급자는 <see cref="ColumnInfo.DefaultValue"/> 로 주고 팩터리가 이리 옮긴다).</summary>
    public string? VirtualExpression { get; init; }

    public string? Comment { get; init; }

    /// <summary>신원 속성(설계 18.5). null = 신원 열 아님.</summary>
    public IdentityInfo? Identity { get; init; }

    /// <summary>
    /// 신원 열인데 공급자가 신원 속성을 읽지 못했다(<see cref="ColumnInfo.IsIdentity"/> 이고 <see cref="ColumnInfo.Identity"/> 가 null). <see cref="Identity"/> 는 null 로 둔다 —
    /// 생성 방식을 짐작하지 않는다. 이런 열은 신원 속성 바꾸기 ✖(신원 없애기만 됨), 「이 구조로」 복사는 신원 없이 옮긴다.
    /// </summary>
    public bool IsIdentityUnknown { get; init; }

    /// <summary>신원 열인가(속성을 알든 모르든).</summary>
    public bool IsIdentityColumn => Identity is not null || IsIdentityUnknown;

    /// <summary>숨김 열(설계 18.5).</summary>
    public bool IsInvisible { get; init; }

    /// <summary>LOB 열의 저장 절(「저장」 페이지 LOB 표, 설계 18.2). LOB 이 아니면 null. 편집 모델의 null 칸은 그대로 둔다(<see cref="TableDraft.Storage"/> 와 같은 규칙).</summary>
    public LobStorage? Lob { get; init; }

    /// <summary>도메인 이름(23ai, 설계 18.5).</summary>
    public string? Domain { get; init; }

    /// <summary>지울 열로 표시(실행 전까지 아무것도 바뀌지 않는다). 새 열은 지우면 목록에서 빼면 되지만 표시만 해도 문장이 없다.</summary>
    public bool IsDropped { get; init; }

    public bool IsVirtual => !string.IsNullOrWhiteSpace(VirtualExpression);
}

/// <summary>
/// 제약 한 줄. 이 테이블 열은 열 줄 키(<see cref="ColumnKeys"/>)로 가리킨다 — 열 이름을 바꿔도 제약이 바뀐 것으로 보지 않고(서버가 따라 바꾼다),
/// 지울 열을 쓰는 제약을 찾을 수 있다. 참조 테이블 쪽은 이름(<see cref="ReferencedColumns"/>)이다.
/// </summary>
public sealed record ConstraintDraft(string RowKey, string Name, ConstraintKind Kind)
{
    /// <summary>이 테이블 열의 줄 키(위치 순서).</summary>
    public IReadOnlyList<string> ColumnKeys { get; init; } = [];

    /// <summary>FK 참조 테이블.</summary>
    public DbObjectRef? ReferencedTable { get; init; }

    /// <summary>FK 가 참조하는 부모 PK·UK 이름(고르는 칸용, 문장에는 쓰지 않는다).</summary>
    public string? ReferencedConstraint { get; init; }

    /// <summary>FK 참조 열 이름(<see cref="ColumnKeys"/> 와 위치로 짝).</summary>
    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public DeleteRule DeleteRule { get; init; }

    /// <summary>CHECK 조건 식.</summary>
    public string? Condition { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsDropped { get; init; }

    /// <summary>이름이 규칙(<c>PK_&lt;테이블&gt;</c> …)으로 채워져 테이블 이름을 따라 바뀌는가. 사용자가 이름을 고치면 false(설계 7.5).</summary>
    public bool IsAutoName { get; init; }
}

/// <summary>인덱스 한 줄. 열은 <see cref="IndexColumnDraft"/>(열 줄 키 또는 식 + DESC).</summary>
public sealed record IndexDraft(string RowKey, string Name)
{
    public bool IsUnique { get; init; }

    public IReadOnlyList<IndexColumnDraft> Columns { get; init; } = [];

    /// <summary>종류(설계 7.4·18.6).</summary>
    public IndexKind Kind { get; init; }

    public string? Tablespace { get; init; }

    /// <summary>이 인덱스를 만든 PK·UK 제약 이름. 있으면 흐리게 두고 제약 페이지에서 고친다(설계 7.4).</summary>
    public string? ConstraintName { get; init; }

    public bool IsDropped { get; init; }

    /// <summary>이름이 규칙(<c>IX_&lt;테이블&gt;_NN</c>)으로 채워져 테이블 이름을 따라 바뀌는가.</summary>
    public bool IsAutoName { get; init; }
}

/// <summary>
/// 인덱스 열 하나. 일반 열은 <see cref="ColumnKey"/>(이 테이블 열 줄 키), 함수 기반 인덱스의 식 자리는 <see cref="Expression"/>(설계 18.6).
/// 둘 중 하나만 쓴다. <see cref="ColumnKey"/> 의 기본값 null 은 작업 저장 JSON(<see cref="TableDraftJson"/>)이 식 자리의 빠진 열 키를 받게 하려는 것.
/// </summary>
public sealed record IndexColumnDraft(string? ColumnKey = null, bool Descending = false)
{
    public string? Expression { get; init; }
}

/// <summary>인덱스 종류(설계 7.4·18.6). 사전 <c>INDEX_TYPE</c> 의 NORMAL·FUNCTION-BASED NORMAL·BITMAP 을 맞춘다. 나머지(도메인·LOB·클러스터 …)는 <see cref="Other"/> 로 지우기만.</summary>
public enum IndexKind
{
    Normal,
    FunctionBased,
    Bitmap,
    Other,
}

/// <summary>객체 상세의 편집 페이지(바뀐 건수 표시·문장 출처).</summary>
public enum TableEditPage
{
    Columns,
    Constraints,
    Indexes,

    /// <summary>「설명」 페이지: 테이블 주석·이름(18.1).</summary>
    Description,

    /// <summary>「저장」 페이지(설계 18.2).</summary>
    Storage,

    /// <summary>「파티션」 페이지(설계 18.4).</summary>
    Partitions,
}

/// <summary>줄 키 만들기. 원본 줄은 팩터리가 종류·순번으로 붙이고(<c>c1</c>, <c>k1</c>, <c>i1</c>), 새 줄은 <see cref="New"/>.</summary>
public static class DraftKeys
{
    /// <summary>새 줄 키(원본 키와 겹치지 않는다).</summary>
    public static string New() => "n" + Guid.NewGuid().ToString("N");
}
