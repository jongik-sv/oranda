using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 객체 상세 「데이터」 탭의 실행 쪽. 데이터 질의·거르기·행 수 세기를 그 상세 문서의 세션으로 실행 엔진
/// (치환 변수 → 정책 관문 → 드라이버)에 보낸다. 결과 거르기 줄(<see cref="GridFilterRunner"/>)과 같은 길을 쓴다:
/// 실행하는 동안 세션을 「실행 중」으로 두어 다른 실행과 겹치지 않게 하고, 앱이 대신 실행하는 질의라 이력에는 남기지 않는다.
/// 실패는 이유를 담은 예외로, 취소는 <see cref="OperationCanceledException"/> 으로 알린다.
/// </summary>
public sealed class ObjectDataRunner
{
    private readonly ExecutionState _state;
    private readonly GridFilterRunner _filter;

    /// <param name="state">실행 상태(편집기와 같은 것, 버스마다 하나)</param>
    /// <param name="binds">바인드 변수 목록(거르기 조건에 쓴 <c>:이름</c> 의 값)</param>
    /// <param name="session">상세 문서의 세션(편집기의 활성 세션이 아니다)</param>
    /// <param name="execution">실행 엔진(부를 때 해석한다)</param>
    public ObjectDataRunner(ExecutionState state, BindVariableStore binds, ISession session, Func<IExecutionService> execution)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(binds);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(execution);
        _state = state;
        Session = session;
        _filter = new GridFilterRunner(state, binds, () => session, execution);
    }

    public ISession Session { get; }

    /// <summary>데이터 질의를 실행해 결과 집합을 넘긴다(수명은 받는 쪽이 가진다).</summary>
    public Task<IResultSet> LoadAsync(SqlStatement query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_state.RunState == SessionRunState.Broken)
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 새로 고치세요.");
        }

        if (_state.Busy)
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 새로 고치세요.");
        }

        return _filter.RunAsync(new ResultFilterRequest(query, string.Empty, string.Empty), ct);
    }

    /// <summary>거르기 줄: 데이터 질의를 <c>SELECT * FROM (…) WHERE … ORDER BY …</c> 로 감싸 실행한다.</summary>
    public Task<IResultSet> FilterAsync(ResultFilterRequest request, CancellationToken ct) => _filter.RunAsync(request, ct);

    /// <summary>전체 행 수(거른 결과면 거른 질의의 행 수). 조건·정렬이 비면 데이터 질의를 그대로 센다.</summary>
    public Task<long?> CountAsync(ResultFilterRequest request, CancellationToken ct) => _filter.CountAsync(request, ct);
}
