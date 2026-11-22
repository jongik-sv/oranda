using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>가짜 claude 준비 서비스(실제 claude·인터넷 없음). 결과를 시험이 정하고 부른 횟수를 센다.</summary>
internal sealed class FakeClaude : IClaudeSetupService
{
    public ClaudeInstallation? Installed { get; set; }

    public int Finds { get; private set; }

    public int Installs { get; private set; }

    public int LoginStarts { get; private set; }

    public int StatusChecks { get; private set; }

    /// <summary>설치 때 흘릴 줄.</summary>
    public List<string> InstallLines { get; } = ["downloading", "installing", "done"];

    /// <summary>설치 결과. null 이면 성공(<see cref="InstallTo"/> 를 설치함).</summary>
    public AiSetupErrorKind? InstallFails { get; set; }

    public ClaudeInstallation InstallTo { get; set; } = new("/home/user/.local/bin/claude", "2.1.330");

    /// <summary>있으면 설치가 이것이 끝날 때까지(또는 취소까지) 멈춘다.</summary>
    public TaskCompletionSource? InstallGate { get; set; }

    public string? LoginUrl { get; set; } = "https://login.example.com/start";

    /// <summary>로그인 시작 뒤 늦게 알릴 주소(시작 결과에는 없음).</summary>
    public string? LateLoginUrl { get; set; }

    public AiSetupErrorKind? LoginStartFails { get; set; }

    public FakeHandle? LastHandle { get; private set; }

    /// <summary>상태 확인 결과 차례(마지막 값이 계속).</summary>
    public Queue<ClaudeLoginStatus> Statuses { get; } = new();

    public ClaudeLoginStatus LastStatus { get; set; } = new(false, null, StatusSupported: true);

    public Task<ClaudeInstallation?> FindAsync(CancellationToken ct)
    {
        Finds++;
        return Task.FromResult(Installed);
    }

    public async Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Installs++;
        foreach (var line in InstallLines)
        {
            progress?.Report(line);
        }

        if (InstallGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (InstallFails is { } kind)
        {
            return new ClaudeInstallResult(false, null, kind, ClaudeInstallCommands.Unix, "exit 1");
        }

        Installed = InstallTo;
        return new ClaudeInstallResult(true, InstallTo, null, ClaudeInstallCommands.Unix);
    }

    public string ManualInstallCommand(AiHostPlatform platform) => ClaudeInstallCommands.ManualFor(platform);

    /// <summary>있으면 로그인 시작이 이것이 끝날 때까지 멈춘다(취소는 무시 — 늦게 돌아오는 서비스 흉내).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>있으면 로그인 프로세스 끝남을 이것으로 알린다.</summary>
    public TaskCompletionSource<int>? Exited { get; set; }

    /// <summary>마지막 로그인 시작이 받은 주소 알림(시험이 늦은 주소를 보낸다).</summary>
    public IProgress<string>? LastUrlProgress { get; private set; }

    /// <summary>만든 손잡이 모두.</summary>
    public List<FakeHandle> Handles { get; } = [];

    public async Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct)
    {
        LoginStarts++;
        LastUrlProgress = browserUrl;
        if (StartGate is { } gate)
        {
            await gate.Task;
        }

        if (LoginStartFails is { } kind)
        {
            return new ClaudeLoginStart(false, null, null, kind);
        }

        LastHandle = new FakeHandle();
        Handles.Add(LastHandle);
        if (LateLoginUrl is { } late)
        {
            browserUrl?.Report(late);
        }

        return new ClaudeLoginStart(true, LoginUrl, LastHandle, Exited: Exited?.Task);
    }

    public Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct)
    {
        StatusChecks++;
        if (Statuses.Count > 0)
        {
            LastStatus = Statuses.Dequeue();
        }

        return Task.FromResult(LastStatus);
    }

    public Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new AiSetupTestResult(true, TimeSpan.FromSeconds(1)));

    internal sealed class FakeHandle : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
