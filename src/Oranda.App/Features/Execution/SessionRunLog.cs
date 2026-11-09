using System.Runtime.CompilerServices;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 세션별 실행 순번: 그 세션에서 실제로 서버에 보낸 문장이 몇 번째인지. 모든 실행 경로(편집기·그리드 필터·행 수 세기·계획 다시 실행·이력 스크립트 …)가
/// 지나는 <see cref="StatementExecuted"/> 에서만 올리므로, 「이 세션의 직전 문장」(DISPLAY_CURSOR(NULL))이 누구 것인지 탭 기록에서 거꾸로 추정하지 않고 바로 안다.
/// 실행 상태(<see cref="ExecutionState"/>)가 만들 때 구독하므로 첫 실행 전에 이미 듣고 있고, 세션이 닫히면 그 항목을 버린다.
/// </summary>
public sealed class SessionRunLog
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _current = [];
    private readonly ConditionalWeakTable<ExecutionResult, object> _sequenceOf = [];

    public SessionRunLog(IEventBus? bus = null)
    {
        bus?.Subscribe<StatementExecuted>(this, OnExecuted);
        bus?.Subscribe<SessionClosed>(this, e =>
        {
            lock (_gate)
            {
                _current.Remove(e.SessionId);
            }
        });
    }

    /// <summary>이 세션에서 지금까지 서버에 보낸 문장 수(= 마지막 문장의 순번). 이 앱에서 보낸 문장이 없으면 null.</summary>
    public long? CurrentSequence(Guid sessionId)
    {
        lock (_gate)
        {
            return _current.TryGetValue(sessionId, out var n) ? n : null;
        }
    }

    /// <summary>실행 결과가 세션에서 몇 번째 문장이었는지(서버에 보내지 않은 결과면 null). 컨트롤러가 시작한 탭에 기록할 때 쓴다.</summary>
    public long? SequenceOf(ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _sequenceOf.TryGetValue(result, out var n) ? (long)n : null;
    }

    /// <summary>
    /// 서버에 문장을 보냈는가. 정책 거부·확인 취소·세션 오류·SQL*Plus 명령(DB 를 부르지 않는다)은 아니다.
    /// 취소는 보내기 전이면 경과 시간이 0 이고 실행 도중이면 경과 시간이 있다.
    /// </summary>
    internal static bool WasSent(ExecutionResult r) =>
        r.Statement.Kind != StatementKind.SqlPlus
        && r.Status switch
        {
            ExecutionStatus.Succeeded => true,
            ExecutionStatus.Failed => r.Error is { Number: not 0 }, // Tibero 드라이버 오류 번호는 음수(TBR-8033 → -8033)
            ExecutionStatus.Cancelled => r.Elapsed > TimeSpan.Zero,
            _ => false,
        };

    private void OnExecuted(StatementExecuted e)
    {
        if (!WasSent(e.Result))
        {
            return;
        }

        lock (_gate)
        {
            var next = (_current.TryGetValue(e.SessionId, out var n) ? n : 0) + 1;
            _current[e.SessionId] = next;
            _sequenceOf.AddOrUpdate(e.Result, next);
        }
    }
}
