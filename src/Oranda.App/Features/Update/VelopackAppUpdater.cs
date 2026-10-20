using System.Diagnostics;
using Oranda.Core.Composition;
using Velopack;
using Velopack.Sources;

namespace Oranda.App.Features.Update;

/// <summary>
/// <see cref="IAppUpdater"/> 의 Velopack 구현. 적용은 <c>WaitExitThenApplyUpdates</c> 만 쓴다: 갱신기를 띄워 두고 앱이 스스로 끝나기를 기다리므로
/// 종료 확인·작업 공간 저장·서비스 정리가 그대로 돈다(<c>ApplyUpdatesAndRestart</c> 는 앱을 즉시 끄므로 쓰지 않는다).
/// 무결성은 Velopack 이 릴리스 메타데이터의 해시로 확인한다(서명 확인이 아니다).
/// </summary>
[OrandaService(typeof(IAppUpdater))]
public sealed class VelopackAppUpdater : IAppUpdater
{
    private readonly object _gate = new();
    private UpdateManager? _manager;
    private UpdateInfo? _info;

    public bool IsInstalled => AppVersion.Installed is not null;

    public string? InstalledVersion => AppVersion.Installed;

    public string? PendingRestart => IsInstalled ? PendingManager().UpdatePendingRestart?.Version.ToString() : null;

    public async Task<UpdateOffer?> CheckAsync(UpdateFeed feed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ct.ThrowIfCancellationRequested();
        var manager = Create(feed);
        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _manager = manager;
            _info = info;
        }

        if (info is null)
        {
            return null;
        }

        var target = info.TargetFullRelease;
        return new UpdateOffer(target.Version.ToString(), target.NotesMarkdown, target.Size);
    }

    public async Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        UpdateManager manager;
        UpdateInfo info;
        lock (_gate)
        {
            if (_manager is null || _info is null || _info.TargetFullRelease.Version.ToString() != offer.Version)
            {
                throw new InvalidOperationException("확인하지 않은 버전은 내려받을 수 없다.");
            }

            manager = _manager;
            info = _info;
        }

        await manager.DownloadUpdatesAsync(info, p => progress?.Report(p), ct).ConfigureAwait(false);
    }

    public void ApplyOnExit() => Apply(restart: false);

    public void ApplyAndRestart() => Apply(restart: true);

    private void Apply(bool restart)
    {
        var manager = PendingManager();
        if (manager.UpdatePendingRestart is not { } asset)
        {
            return;
        }

        // 갱신기는 설치 폴더의 실행 중인 프로세스를 모두 강제로 끝내고 적용한다. 같은 설치본을 쓰는 다른 Oranda 창(저장 안 한 탭·미커밋 트랜잭션)이
        // 닫기 확인 없이 죽지 않도록, 다른 인스턴스가 있으면 적용하지 않고 둔다(받아 둔 업데이트는 다음 시작 때 「준비됨」으로 남는다).
        if (OtherInstanceRunning())
        {
            Trace.TraceWarning("다른 Oranda 가 실행 중이라 업데이트 적용을 미룬다.");
            return;
        }

        manager.WaitExitThenApplyUpdates(asset, silent: !restart, restart: restart);
    }

    private static bool OtherInstanceRunning()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var path = Environment.ProcessPath;
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id)
                    {
                        continue;
                    }

                    string? otherPath;
                    try
                    {
                        otherPath = other.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        return true; // 경로를 못 읽는 같은 이름의 프로세스는 다른 인스턴스로 본다
                    }

                    if (string.Equals(otherPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("다른 인스턴스를 찾지 못했다: {0}", ex.Message);
        }

        return false;
    }

    /// <summary>내려받은 업데이트를 알아내거나 적용하는 데는 피드가 필요 없다(로컬 패키지 폴더만 본다).</summary>
    private UpdateManager PendingManager()
    {
        lock (_gate)
        {
            return _manager ??= new UpdateManager(Path.GetTempPath());
        }
    }

    private static UpdateManager Create(UpdateFeed feed) => feed.Kind switch
    {
        UpdateFeedKind.GitHub => new UpdateManager(new GithubSource(feed.Location, null, false)),
        _ => new UpdateManager(feed.Location),
    };
}
