using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>외래키 따라가기 방향(GRID-11). PL/SQL Developer 「Linked Query」·DataGrip 「Related Rows」와 같은 일.</summary>
public enum RelatedRowsKind
{
    /// <summary>이 행이 참조하는 부모 행(<c>oranda.grid.followForeignKey</c>): 활성 칸 열이 든 외래키로 부모 테이블을 조회한다.</summary>
    Parent,

    /// <summary>이 행을 참조하는 자식 행(<c>oranda.grid.showReferencingRows</c>): 이 테이블의 키를 참조하는 외래키로 자식 테이블을 조회한다.</summary>
    Children,
}

/// <summary>
/// 외래키 따라가기 요청: 결과를 만든 질의 <see cref="Query"/>, 결과 열, 활성 칸의 열 번호(<see cref="Ordinal"/>, 결과 열 번호),
/// 그 행의 값(<see cref="Row"/>, 결과 열 번호 순서). 값은 결과 영역이 UI 스레드에서 미리 읽어 둔다.
/// </summary>
public sealed record RelatedRowsRequest(RelatedRowsKind Kind, SqlStatement Query, IReadOnlyList<ResultColumn> Columns, int Ordinal,
                                        IReadOnlyList<object?> Row);

/// <summary>
/// 외래키 따라가기 결과: 새 결과 탭에 열 결과 집합(수명은 받는 쪽이 가진다)과 탭 제목, 그 결과를 만든 문장과 문장의 바인드 값.
/// 바인드 값은 새 결과 영역이 가지고 있다가 거르기·전체 행 수 세기에 함께 넘긴다(「바인드 변수」 목록에는 넣지 않는다).
/// </summary>
public sealed record RelatedRowsResult(IResultSet Result, string Title, SqlStatement Statement, IReadOnlyDictionary<string, BindValue> Binds);
