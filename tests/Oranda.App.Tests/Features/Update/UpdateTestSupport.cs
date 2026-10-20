using Oranda.App.Features.Update;
using Oranda.App.Shell.Settings;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.Update;

/// <summary>저장하지 않고 메모리에만 두는 설정 저장소.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Saved { get; private set; } = new();

    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Saved));

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        Saved = settings;
        return Task.CompletedTask;
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>시험용 가짜 업데이트 구현. 호출 횟수를 센다.</summary>
internal sealed class FakeUpdater : IAppUpdater
{
    public bool IsInstalled { get; set; } = true;

    public string? InstalledVersion { get; set; } = "0.1.5";

    public string? PendingRestart { get; set; }

    public UpdateOffer? Offer { get; set; }

    public Exception? CheckFails { get; set; }

    /// <summary>값이 있으면 확인이 이 작업이 끝날 때까지 멈춘다(겹친 확인 시험).</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public Exception? DownloadFails { get; set; }

    /// <summary>값이 있으면 내려받기가 이 작업이 끝날 때까지 멈춘다(취소 시험).</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public int CheckCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public int ApplyOnExitCalls { get; private set; }

    public int ApplyAndRestartCalls { get; private set; }

    public UpdateFeed? LastFeed { get; private set; }

    public async Task<UpdateOffer?> CheckAsync(UpdateFeed feed, CancellationToken ct)
    {
        CheckCalls++;
        LastFeed = feed;
        if (CheckGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return CheckFails is { } ex ? throw ex : Offer;
    }

    public async Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct)
    {
        DownloadCalls++;
        if (DownloadFails is { } ex)
        {
            throw ex;
        }

        progress?.Report(40);
        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        progress?.Report(100);
        PendingRestart = offer.Version;
    }

    public void ApplyOnExit() => ApplyOnExitCalls++;

    public void ApplyAndRestart() => ApplyAndRestartCalls++;
}

/// <summary>
/// 가짜 닫기 흐름. <see cref="Passes"/> 가 true 면 실제 흐름처럼 확인을 통과한 뒤 등록된 종료 처리기를 부르고 true,
/// false 면 사용자가 취소한 것처럼 처리기를 부르지 않고 false.
/// </summary>
internal sealed class FakeExitFlow : IExitFlow
{
    private Action? _handler;

    public bool Passes { get; set; } = true;

    public int Requests { get; private set; }

    public void OnExit(Action handler) => _handler += handler;

    public Task<bool> RequestExitAsync()
    {
        Requests++;
        if (Passes)
        {
            _handler?.Invoke();
        }

        return Task.FromResult(Passes);
    }

    /// <summary>⌘Q·창 닫기 등 평범한 종료(확인 통과 후)를 흉내 낸다.</summary>
    public void Quit() => _handler?.Invoke();
}

internal sealed class UpdateHarness
{
    public UpdateHarness()
    {
        Store = new MemorySettingsStore();
        Settings = new SettingsService(Store);
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        Updater = new FakeUpdater { Offer = new UpdateOffer("0.1.9", "- 고침", 1234) };
        Exit = new FakeExitFlow();
        Service = new UpdateService(Updater, Settings, Exit) { Time = Time, DefaultFeed = "https://updates.example.com/oranda" };
    }

    public MemorySettingsStore Store { get; }

    public SettingsService Settings { get; }

    public ManualTimeProvider Time { get; }

    public FakeUpdater Updater { get; }

    public FakeExitFlow Exit { get; }

    public UpdateService Service { get; }

    public Task Configure(Func<UpdateSettings, UpdateSettings> change) =>
        Settings.UpdateAsync(s => s with { Update = change(s.Update) });
}
