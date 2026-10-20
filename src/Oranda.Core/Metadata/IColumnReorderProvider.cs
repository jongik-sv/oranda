using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 열 순서 바꾸기(TSK-13-01)가 서버에서 읽는 정보. 서버에 보조 객체를 설치하지 않고 딕셔너리 뷰만 읽는다.
/// </summary>
public interface IColumnReorderProvider
{
    /// <summary>표 종류(외부·임시·클러스터). INVISIBLE 열을 쓸 수 없는 표를 가려내는 데 쓴다.</summary>
    Task<ColumnReorderTraits> GetTraitsAsync(ISession s, DbObjectRef table, CancellationToken ct);

    /// <summary>
    /// 표를 참조하는 PL/SQL 소스(<c>all_dependencies</c>) 가운데 <c>SELECT</c>·<c>INSERT</c> 가 나오는 줄.
    /// 어떤 줄이 위험한지는 호출한 쪽이 판정한다(한 줄 단위라 여러 줄에 걸친 문장은 후보에서 빠진다).
    /// </summary>
    Task<IReadOnlyList<SourceSearchHit>> FindReferencingSourceLinesAsync(ISession s, DbObjectRef table, CancellationToken ct);
}

/// <summary>표 종류. 모두 false 면 일반 힙 표(또는 IOT)다.</summary>
public sealed record ColumnReorderTraits(bool IsExternal, bool IsTemporary, string? ClusterName)
{
    public bool IsClustered => !string.IsNullOrEmpty(ClusterName);
}
