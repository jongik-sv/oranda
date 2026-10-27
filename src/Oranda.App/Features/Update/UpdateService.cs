using System.Diagnostics;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Update;

/// <summary>업데이트 흐름의 지금 단계.</summary>
public enum UpdatePhase
{
    /// <summary>아직 확인하지 않았거나, 시작 확인이 조용히 끝났다(알릴 것 없음).</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>새 버전을 찾았다(내려받기 전).</summary>
    Available,

    Downloading,

    /// <summary>내려받기를 마쳐 적용만 남았다.</summary>
    Ready,

    /// <summary>확인·내려받기가 실패했다(<see cref="UpdateService.ErrorDetail"/>).</summary>
    Error,

    /// <summary>설치하지 않은 실행본(개발 빌드)이라 쓸 수 없다.</summary>
    NotInstalled,

    /// <summary>피드 주소가 없다(설정도 빌드 기본값도 비었다).</summary>
    NoFeed,

    /// <summary>설정의 피드 주소를 해석하지 못했다(<see cref="UpdateService.FeedError"/>).</summary>
    InvalidFeed,
}

/// <summary>내려받은 업데이트를 언제 적용할지.</summary>
public enum UpdateApplyMode
{
    None,

    /// <summary>앱을 끝낼 때(다시 띄우지 않는다).</summary>
    OnExit,

    /// <summary>「지금 다시 시작해 적용」: 닫기 흐름을 통과하면 적용하고 다시 띄운다.</summary>
    Restart,
}

/// <summary>
/// 메인 창의 기존 닫기 흐름(저장 안 한 탭·미커밋 트랜잭션 확인, 전용 세션 정리, 레이아웃·작업 공간 저장)을 업데이트가 빌려 쓰는 입구.
/// 적용은 반드시 이 흐름을 모두 통과한 뒤에만 일어난다.
/// </summary>
public interface IExitFlow
{
    /// <summary>모든 종료 확인을 통과한 뒤, 창이 닫히기 직전에 부를 처리기를 등록한다(어떤 경로의 종료든 부른다).</summary>
    void OnExit(Action handler);

    /// <summary>창 닫기를 요청한다. 닫기 흐름을 통과해 앱이 끝나게 되면 true, 사용자가 취소하면 false.</summary>
    Task<bool> RequestExitAsync();
}

/// <summary>
/// 업데이트 확인·내려받기·적용의 상태 기계. 앱에 하나뿐이며(싱글턴) 대화상자·상태 표시줄·설정 화면·시작 확인이 같은 값을 본다.
/// 상태가 바뀌면 <see cref="Changed"/> 가 임의 스레드에서 나므로 화면은 UI 스레드로 옮겨 읽는다.
/// 적용은 두 단계다: 종료 확인을 모두 통과하면 처리기가 적용 방식을 <b>예약</b>만 하고(<see cref="OnExitConfirmed"/>),
/// 앱이 완전히 끝난 뒤 <c>Program.Main</c> 이 <see cref="CompleteExit"/> 로 갱신기를 띄운다. 갱신기는 60초만 기다리고 그 뒤에는
/// 실행 중인 앱을 강제로 끝내므로, 작업 공간 저장·서비스 정리가 모두 끝난 뒤에 띄워야 한다.
/// </summary>
[OrandaService(typeof(UpdateService))]
public sealed class UpdateService : IDisposable
{
    /// <summary>시작 확인은 마지막 확인 뒤 이만큼 지나야 다시 한다.</summary>
    public static readonly TimeSpan StartupInterval = TimeSpan.FromHours(24);

    /// <summary>메인 창이 뜬 뒤 시작 확인까지 기다리는 시간.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>응답 없는 서버(UNC 공유·웹)에서 확인이 끝없이 매달리지 않게 하는 상한.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    private readonly IAppUpdater _updater;
    private readonly SettingsService _settings;
    private readonly IExitFlow _exit;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _download;
    private Task? _check;
    private bool _manualWanted;
    private Action? _exitAction;

    public UpdateService(IAppUpdater updater, SettingsService settings, IExitFlow exit)
    {
        _updater = updater;
        _settings = settings;
        _exit = exit;
        DefaultFeed = AppVersion.BuildDefaultFeed;
        Current = this;
        exit.OnExit(OnExitConfirmed);
    }

    /// <summary>
    /// 앱에 하나뿐인 서비스(설정 화면·<c>Program.Main</c> 처럼 주입받기 어려운 곳이 쓴다). 조립이 만들 때 채워진다.
    /// 컨테이너가 정리된 뒤에도 남겨 둔다: 종료 마무리(<see cref="CompleteExit"/>)가 그 뒤에 돈다.
    /// </summary>
    public static UpdateService? Current { get; private set; }

    /// <summary>시각 원천. 시험이 바꾼다(컨테이너가 모르는 형식을 생성자로 받으면 지연 어셈블리 훑기가 일어나므로 속성으로 둔다).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>설정의 피드 주소가 비었을 때 쓸 기본값(빌드 때 넣은 값). 시험이 바꾼다.</summary>
    public string? DefaultFeed { get; set; }

    public bool IsInstalled => _updater.IsInstalled;

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    /// <summary>찾은 새 버전. <see cref="UpdatePhase.Available"/>·<see cref="UpdatePhase.Downloading"/>·<see cref="UpdatePhase.Ready"/> 에서 값이 있다.</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>적용만 남은 버전(방금 받은 것 또는 앞선 실행에서 받아 둔 것).</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>내려받기 진행률(0~100).</summary>
    public int Progress { get; private set; }

    /// <summary>찾은 버전이 「건너뛴 버전」인지.</summary>
    public bool IsSkipped { get; private set; }

    public UpdateApplyMode ApplyMode { get; private set; }

    public UpdateFeedError FeedError { get; private set; }

    /// <summary>오류 원문(예외 형식과 메시지). 화면은 툴팁으로 보인다.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>오류 종류. 화면은 종류별 쉬운 문구를 보인다(<see cref="UpdateFailure.Other"/> 면 원문).</summary>
    public UpdateFailure ErrorKind { get; private set; }

    /// <summary>오류가 확인 중에 났는지(false 면 내려받기 중).</summary>
    public bool ErrorWhileDownloading { get; private set; }

    /// <summary>지금 보이는 현재 버전 글.</summary>
    public string CurrentVersionText => _updater.InstalledVersion ?? AppVersion.Display;

    public DateTimeOffset? LastCheckedUtc => _settings.Current.Update.LastCheckedUtc;

    public event EventHandler? Changed;

    /// <summary>설정·빌드 기본값에서 쓸 피드를 정한다. 둘 다 비면 <c>(null, None)</c>, 설정 값이 잘못이면 <c>(null, 이유)</c>.</summary>
    public (UpdateFeed? Feed, UpdateFeedError Error) ResolveFeed()
    {
        var configured = _settings.Current.Update.FeedUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parsed = UpdateFeedParser.Parse(configured);
            return (parsed.Feed, parsed.Error);
        }

        if (!string.IsNullOrWhiteSpace(DefaultFeed))
        {
            var parsed = UpdateFeedParser.Parse(DefaultFeed);
            return (parsed.Feed, parsed.Error);
        }

        return (null, UpdateFeedError.None);
    }

    /// <summary>
    /// 시작할 때 한 번: 앞선 실행에서 받아 둔 업데이트가 있으면 「준비됨」으로 두고(종료할 때 적용), 아니면 조건이 맞을 때
    /// <paramref name="delay"/> 뒤 조용히 확인한다. 지연 동안 사용자가 직접 확인했거나 설정이 바뀌었으면 조건을 다시 보고 물러난다.
    /// 실패는 로그만 남긴다. 부르는 쪽이 기다리지 않아도 되도록 설계했다.
    /// </summary>
    public async Task RunStartupAsync(TimeSpan delay, CancellationToken ct = default)
    {
        if (!_updater.IsInstalled)
        {
            return;
        }

        if (PendingVersion() is { } pending)
        {
            MarkReady(pending);
            return;
        }

        if (!StartupCheckDue())
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, Time, linked.Token).ConfigureAwait(false);
                if (Phase != UpdatePhase.Idle || !StartupCheckDue())
                {
                    return; // 지연 동안 상황이 바뀌었다(직접 확인·설정 변경)
                }
            }

            await CheckAsync(manual: false, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 앱이 끝나는 중이다.
        }
    }

    /// <summary>
    /// 새 버전을 확인한다. <paramref name="manual"/> 이 false(시작 확인)면 실패·건너뛴 버전·최신 버전을 화면에 알리지 않고
    /// (<see cref="UpdatePhase.Idle"/> 그대로), 새 버전이 있고 자동 내려받기가 켜져 있으면 이어서 내려받는다.
    /// 조용한 확인이 도는 동안 사용자가 직접 확인하면 그 확인을 사용자의 것으로 올려 결과를 보인다.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        Task running;
        var announce = false;
        lock (_gate)
        {
            if (Phase is UpdatePhase.Ready or UpdatePhase.Downloading)
            {
                return; // 이미 받았거나 받는 중이다
            }

            if (_check is { IsCompleted: false } existing)
            {
                running = existing;
                if (manual && !_manualWanted)
                {
                    _manualWanted = true;
                    announce = true;
                }
            }
            else
            {
                _manualWanted = manual;
                running = _check = CheckCoreAsync(ct);
            }
        }

        if (announce)
        {
            Set(UpdatePhase.Checking);
        }

        await running.ConfigureAwait(false);
        if (!manual && !_manualWanted && Phase == UpdatePhase.Available && _settings.Current.Update.AutoDownload)
        {
            await DownloadAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        if (!_updater.IsInstalled)
        {
            if (_manualWanted)
            {
                Set(UpdatePhase.NotInstalled);
            }

            return;
        }

        if (PendingVersion() is { } pending)
        {
            MarkReady(pending);
            return;
        }

        var (feed, error) = ResolveFeed();
        if (feed is null)
        {
            if (_manualWanted)
            {
                FeedError = error;
                Set(error == UpdateFeedError.None ? UpdatePhase.NoFeed : UpdatePhase.InvalidFeed);
            }

            return;
        }

        if (_manualWanted)
        {
            Set(UpdatePhase.Checking);
        }

        UpdateOffer? offer;
        try
        {
            offer = await _updater.CheckAsync(feed, ct).WaitAsync(CheckTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (Phase == UpdatePhase.Checking)
            {
                Set(UpdatePhase.Idle);
            }

            return;
        }
        catch (Exception ex)
        {
            var kind = LogFailure("확인", feed, ex);
            if (_manualWanted)
            {
                ErrorDetail = UpdateFailures.Describe(ex);
                ErrorKind = kind;
                ErrorWhileDownloading = false;
                Set(UpdatePhase.Error);
            }

            return;
        }

        await RecordCheckedAsync().ConfigureAwait(false);
        if (offer is null)
        {
            Offer = null;
            IsSkipped = false;
            if (_manualWanted)
            {
                Set(UpdatePhase.UpToDate);
            }

            return;
        }

        Offer = offer;
        IsSkipped = string.Equals(_settings.Current.Update.SkippedVersion, offer.Version, StringComparison.Ordinal);
        if (!_manualWanted && IsSkipped)
        {
            return; // 건너뛴 버전은 시작 확인에서 알리지도 받지도 않는다
        }

        Set(UpdatePhase.Available);
    }

    /// <summary>찾은 버전을 내려받는다. 취소하면 「있음」으로 돌아가고, 실패하면 오류로 바뀐다(다시 누르면 재시도).</summary>
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        UpdateOffer target;
        lock (_gate)
        {
            if (Offer is not { } offer || Phase is not (UpdatePhase.Available or UpdatePhase.Error) || _download is not null)
            {
                return;
            }

            target = offer;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            _download = cts;
        }

        try
        {
            Progress = 0;
            Set(UpdatePhase.Downloading);
            await _updater.DownloadAsync(target, new SyncProgress(OnProgress), cts.Token).ConfigureAwait(false);
            MarkReady(target.Version);
        }
        catch (OperationCanceledException)
        {
            Progress = 0;
            Set(UpdatePhase.Available);
        }
        catch (Exception ex)
        {
            ErrorKind = LogFailure("내려받기", ResolveFeed().Feed, ex);
            ErrorDetail = UpdateFailures.Describe(ex);
            ErrorWhileDownloading = true;
            Progress = 0;
            Set(UpdatePhase.Error);
        }
        finally
        {
            lock (_gate)
            {
                _download = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>내려받는 중이면 취소한다.</summary>
    public void CancelDownload()
    {
        lock (_gate)
        {
            _download?.Cancel();
        }
    }

    /// <summary>「종료할 때 적용」: 앱이 끝날 때(종료 확인을 모두 통과한 뒤) 적용한다. 내려받기를 마치면 이미 이 상태다.</summary>
    public void ApplyOnExit()
    {
        if (Phase == UpdatePhase.Ready)
        {
            ApplyMode = UpdateApplyMode.OnExit;
            RaiseChanged();
        }
    }

    /// <summary>
    /// 「지금 다시 시작해 적용」: 메인 창의 닫기 흐름을 먼저 돌리고, 사용자가 취소하면 아무것도 적용하지 않는다.
    /// 통과하면 닫기 직전에 적용 방식이 예약되고 앱이 끝난 뒤 갱신기가 적용하고 다시 띄운다. 닫기 흐름을 통과했으면 true.
    /// </summary>
    public async Task<bool> ApplyAndRestartAsync()
    {
        if (Phase != UpdatePhase.Ready)
        {
            return false;
        }

        var before = ApplyMode;
        ApplyMode = UpdateApplyMode.Restart;
        bool passed;
        try
        {
            passed = await _exit.RequestExitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError("닫기 흐름 실패: {0}", ex);
            passed = false;
        }

        if (!passed)
        {
            ApplyMode = before; // 취소했는데 다음 평범한 종료가 다시 뜨게 만들면 안 된다
            RaiseChanged();
        }

        return passed;
    }

    /// <summary>찾은 버전을 시작 확인에서 알리지 않게 한다.</summary>
    public async Task SkipVersionAsync()
    {
        if (Offer is not { } offer || Phase is not (UpdatePhase.Available or UpdatePhase.Error))
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { Update = s.Update with { SkippedVersion = offer.Version } }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("건너뛴 버전을 저장하지 못했다: {0}", ex.Message);
            return;
        }

        IsSkipped = true;
        RaiseChanged();
    }

    /// <summary>설정 화면이 저장한 뒤 부른다: 「건너뛴 버전」 표시를 지금 설정에 맞춘다.</summary>
    public void RefreshSettings()
    {
        IsSkipped = Offer is { } offer && string.Equals(_settings.Current.Update.SkippedVersion, offer.Version, StringComparison.Ordinal);
        RaiseChanged();
    }

    /// <summary>
    /// 앱이 완전히 끝난 뒤(<c>Program.Main</c> 이 UI 루프에서 돌아온 직후) 예약해 둔 적용을 갱신기에 넘긴다. 종료 확인을 통과하지 않았으면 아무것도 하지 않는다.
    /// </summary>
    public void CompleteExit()
    {
        var action = Interlocked.Exchange(ref _exitAction, null);
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Trace.TraceError("업데이트 적용 예약 실패: {0}", ex);
        }
    }

    public void Dispose()
    {
        CancelDownload();
        _life.Cancel();
    }

    // 닫기 흐름을 모두 통과한 뒤, 창이 닫히기 직전에 불린다: 백그라운드 확인이 종료 중 설정을 쓰지 않게 멈추고 적용 방식만 예약한다.
    private void OnExitConfirmed()
    {
        _life.Cancel();
        if (Phase != UpdatePhase.Ready)
        {
            return;
        }

        _exitAction = ApplyMode switch
        {
            UpdateApplyMode.OnExit => _updater.ApplyOnExit,
            UpdateApplyMode.Restart => _updater.ApplyAndRestart,
            _ => null,
        };
    }

    /// <summary>실패를 앱 로그(<c>logs/oranda-yyyyMMdd.log</c>)에 한 줄 남긴다: 시각·종류·피드·원문.</summary>
    private UpdateFailure LogFailure(string step, UpdateFeed? feed, Exception ex)
    {
        var kind = UpdateFailures.Classify(ex);
        Trace.TraceWarning(
            "{0:yyyy-MM-dd HH:mm:ss} 업데이트 {1} 실패 [{2}] 피드={3}: {4}",
            Time.GetLocalNow(), step, kind, feed?.Location ?? "-", UpdateFailures.Describe(ex));
        return kind;
    }

    private bool StartupCheckDue()
    {
        var options = _settings.Current.Update;
        if (!options.CheckOnStartup || ResolveFeed().Feed is null)
        {
            return false;
        }

        // 마지막 확인이 미래 시각이면(시계를 되돌렸다) 때가 된 것으로 본다.
        var now = Time.GetUtcNow();
        return options.LastCheckedUtc is not { } last || last > now || now - last >= StartupInterval;
    }

    private string? PendingVersion()
    {
        try
        {
            return _updater.PendingRestart;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("받아 둔 업데이트를 확인하지 못했다: {0}", ex.Message);
            return null;
        }
    }

    private void OnProgress(int percent)
    {
        if (Phase == UpdatePhase.Downloading)
        {
            Progress = Math.Clamp(percent, 0, 100);
            RaiseChanged();
        }
    }

    private void MarkReady(string version)
    {
        ReadyVersion = version;
        Progress = 100;
        ApplyMode = UpdateApplyMode.OnExit; // 기본 = 종료할 때 적용
        Set(UpdatePhase.Ready);
    }

    private async Task RecordCheckedAsync()
    {
        try
        {
            var now = Time.GetUtcNow();
            await _settings.UpdateAsync(s => s with { Update = s.Update with { LastCheckedUtc = now } }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("마지막 업데이트 확인 시각을 저장하지 못했다: {0}", ex.Message);
        }
    }

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        RaiseChanged();
    }

    // 받는 쪽 처리기 하나가 예외를 던져도 상태 기계가 중간에 멈추지 않게 처리기마다 따로 부른다.
    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceError("업데이트 상태 알림 처리기 실패: {0}", ex);
            }
        }
    }

    /// <summary><see cref="Progress{T}"/> 는 보고를 UI 컨텍스트로 미뤄 끝난 뒤에 도착할 수 있으므로, 순서가 보장되는 동기 보고자를 쓴다.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
