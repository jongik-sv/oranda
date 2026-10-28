using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>TSK-14-17 세션 모니터 「튜닝 추천」 탭을 잇는 부분. 검사·표시는 <see cref="SessionTuningHintsViewModel"/>.</summary>
public sealed partial class SessionMonitorViewModel
{
    private SessionTuningHintsViewModel? _hints;
    private ITuningService? _tuning;

    /// <summary>활성 세션 SQL 튜닝 추천 탭.</summary>
    public SessionTuningHintsViewModel TuningHints => _hints ??= new SessionTuningHintsViewModel(this);

    /// <summary>튜닝 조회 서비스. 시험이 넣고, 셸은 처음 쓸 때 DI 에서 꺼낸다.</summary>
    public ITuningService? TuningService
    {
        get => _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService;
        set => _tuning = value;
    }

    /// <summary>명령이 등록돼 있는지(SQL 모니터 바로 가기). 시험이 넣고, 없으면 셸 명령 레지스트리로 본다.</summary>
    public Func<string, bool>? CommandAvailable { get; set; }

    /// <summary>명령 실행(명령 ID, 인자). 시험이 넣고, 없으면 셸 명령 레지스트리로.</summary>
    public Func<string, object?, Task>? RunCommand { get; set; }

    internal bool CanRunCommand(string id) => CommandAvailable is { } available
        ? available(id)
        : _services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && registry.Find(id) is not null;

    internal Task RunShellCommandAsync(string id, object? parameter)
    {
        if (RunCommand is { } run)
        {
            return run(id, parameter);
        }

        return _services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && _services.GetService(typeof(ContextKeyService)) is ContextKeyService keys
            ? registry.ExecuteAsync(id, keys, parameter)
            : Task.CompletedTask;
    }

    /// <summary>튜닝 조회 한 번(목록 새로 고침과 같은 관문). 취소는 던지고, 그 밖의 실패는 <paramref name="fail"/> 로.</summary>
    internal async Task<bool> RunTuningAsync(Func<ITuningService, ISession, Task> work, Action<Exception> fail, CancellationToken ct)
    {
        if (_session is not { } session || TuningService is not { } tuning)
        {
            fail(new InvalidOperationException(_session is null ? "모니터 세션이 열려 있지 않습니다." : "튜닝 서비스가 없습니다."));
            return false;
        }

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await work(tuning, session);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fail(ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
