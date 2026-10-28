using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>TSK-14-17 「튜닝 추천」 탭을 SQL 모니터에 잇는 부분. 조회·규칙은 <see cref="TuningRecommendationsViewModel"/>.</summary>
public sealed partial class SqlMonitorViewModel
{
    /// <summary>「튜닝 추천」 탭 번호(아래 탭의 마지막).</summary>
    public const int RecommendTab = 5;

    /// <summary>튜닝 메뉴 「튜닝 추천」 명령. 인자로 sql_id 문자열을 주면 그 SQL 을 골라 보인다(세션 모니터의 바로 가기).</summary>
    public const string RecommendCommand = "oranda.tuning.recommend";

    private TuningRecommendationsViewModel? _recommend;

    /// <summary>튜닝 추천 탭.</summary>
    public TuningRecommendationsViewModel Recommendations => _recommend ??= new TuningRecommendationsViewModel(this);

    /// <summary>모니터 세션의 접속 프로필 Id(편집기 탭 연결용). 세션이 없으면 null.</summary>
    internal Guid? MonitorProfileId => _session?.Profile.Id;

    /// <summary>
    /// 모니터 서비스 조회 한 번(전문 읽기). Top SQL 검색·튜닝 조회와 같은 관문을 지난다. 취소는 던지고, 그 밖의 실패는 <paramref name="fail"/> 로.
    /// </summary>
    internal async Task<bool> RunMonitorQueryAsync(Func<IMonitorService, ISession, Task> work, Action<Exception> fail, CancellationToken ct)
    {
        var monitor = _monitor ??= _services?.GetService(typeof(IMonitorService)) as IMonitorService;
        if (_session is not { } session || monitor is null)
        {
            fail(new InvalidOperationException(_session is null ? "모니터 세션이 열려 있지 않습니다." : "모니터 서비스가 없습니다."));
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
            await work(monitor, session);
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

    /// <summary>명령이 등록돼 있는가(튜닝 도우미·자식 커서 단추).</summary>
    internal bool CanRunCommand(string id) => IsCommandAvailable(id);

    /// <summary>명령 실행(셸 레지스트리 또는 시험의 <see cref="RunCommand"/>).</summary>
    internal Task RunShellCommandAsync(string id, object? parameter) => Run(id, parameter);

    /// <summary>새 편집기 탭에 글을 넣는다(실행하지 않는다).</summary>
    internal void OpenSqlText(string title, string body) => OpenScript(title, body);
}
