using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>전체 기본값 「대상 처리」: 자동 = 규칙 종류별 기본값(<see cref="TargetActions.DefaultFor"/>).</summary>
public enum DefaultActionChoice
{
    Auto,
    DeleteAllInsert,
    DeleteRangeInsert,
    AppendOnly,
    MergeByPk,
}
