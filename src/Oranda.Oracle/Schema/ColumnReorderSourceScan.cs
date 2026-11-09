using Oranda.Core.Metadata;
using Oranda.Core.Schema;

namespace Oranda.Oracle.Schema;

/// <summary>
/// 표를 참조하는 소스 줄에서 열 순서에 민감한 문장 후보를 가려낸다(한 줄 단위 판정이라 줄을 넘는 문장은 못 찾는다).
/// 내용은 Core <see cref="ColumnReorderSourceFilter"/> 가 정본이고, 여기는 Oracle 의 공개 표면을 그대로 두고 호출만 넘긴다. 소유: TSK-13-01.
/// </summary>
public static class ColumnReorderSourceScan
{
    /// <summary>한 줄을 판정한다. 위험 후보가 아니면 null.</summary>
    public static ColumnReorderRisk? Classify(string line, DbObjectRef table) => ColumnReorderSourceFilter.Classify(line, table);

    /// <summary>소스 줄 가운데 위험 후보만 남긴다.</summary>
    public static IReadOnlyList<ColumnReorderRiskHit> Filter(IEnumerable<SourceSearchHit> lines, DbObjectRef table) =>
        ColumnReorderSourceFilter.Filter(lines, table);
}
