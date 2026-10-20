using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 편집기 밖에서 결과 영역을 쓰는 쪽(객체 상세 「데이터」 탭)이 그 결과의 행 수 세기·거르기·「편집 모드로 다시 실행」을
/// 자기 세션으로 돌리게 하는 연결. <see cref="SqlResultPaneViewModel.Runner"/> 에 채우면 공용 연결
/// (<see cref="ResultGridCommands"/>·<c>GridEditCommands.Rerun</c> — 편집기의 활성 세션) 대신 이것을 쓴다.
/// 계약(실패는 이유를 담은 예외, 취소는 <see cref="OperationCanceledException"/>)은 공용 연결과 같다.
/// </summary>
public sealed class ResultPaneRunner
{
    /// <summary>「전체 행 수 세기」(<see cref="ResultGridCommands.CountRows"/> 와 같은 계약).</summary>
    public required Func<SqlStatement, CancellationToken, Task<long?>> CountRows { get; init; }

    /// <summary>결과 거르기(<see cref="ResultGridCommands.Filter"/> 와 같은 계약, 결과 집합의 수명은 결과 영역이 가진다).</summary>
    public required Func<ResultFilterRequest, CancellationToken, Task<IResultSet>> Filter { get; init; }

    /// <summary>거른 결과의 전체 행 수(<see cref="ResultGridCommands.CountFiltered"/> 와 같은 계약).</summary>
    public required Func<ResultFilterRequest, CancellationToken, Task<long?>> CountFiltered { get; init; }

    /// <summary>읽기 전용 결과의 안내 막대 「편집 모드로 다시 실행」(인자는 이 결과를 만든 문장).</summary>
    public required Func<SqlStatement?, CancellationToken, Task> RerunForEdit { get; init; }
}
