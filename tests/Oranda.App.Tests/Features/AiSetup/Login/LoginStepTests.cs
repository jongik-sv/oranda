using Avalonia.Threading;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Login;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Login;

/// <summary>
/// U4 로그인 단계(가짜 서비스·가짜 시계): 이미 로그인, 몇 번 뒤 로그인됨 → 자동 다음, 주소 복사, 상태 확인 미지원 → 단추, 떠나면 주기 멈춤,
/// 시작 중 떠남·연타·프로세스 끝남·늦은 확인. 모두 UI 스레드 하나(Headless)에서 돌려 순서가 정해져 있다.
/// </summary>
public sealed class LoginStepTests
{
    /// <summary>가짜 시계: 기다리기를 시험이 하나씩 끝낸다.</summary>
    private sealed class FakeClock
    {
        private readonly Queue<TaskCompletionSource> _waits = new();

        public int Pending => _waits.Count(w => !w.Task.IsCompleted);

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            var wait = new TaskCompletionSource();
            ct.Register(() => wait.TrySetCanceled(ct));
            _waits.Enqueue(wait);
            return wait.Task;
        }

        /// <summary>가장 앞 살아 있는 기다리기를 끝내고, 이어지는 일을 UI 스레드에서 다 돌린다.</summary>
        public void Tick()
        {
            while (_waits.Count > 0)
            {
                if (_waits.Dequeue().TrySetResult())
                {
                    break;
                }
            }

            Settle();
        }
    }

    private static void Settle() => HeadlessHost.Pump(10);

    private static ClaudeLoginStatus Out => new(false, null, StatusSupported: true);

    private static ClaudeLoginStatus In => new(true, "user@example.com", StatusSupported: true);

    private static AiSetupContext Context(IClaudeSetupService claude, List<string>? copied = null) =>
        new(new SettingsService(new InMemorySettingsStore()))
        {
            Claude = claude,
            Text = new ShellText(language: "ko"),
            CopyText = text =>
            {
                copied?.Add(text);
                return Task.CompletedTask;
            },
        };

    [Fact]
    public Task Already_logged_in_shows_the_account_and_completes() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { LastStatus = In };
        using var step = new LoginStep(Context(claude), new FakeClock().Delay);
        await step.EnterAsync(CancellationToken.None);
        Assert.Equal(LoginState.LoggedIn, step.State);
        Assert.True(step.IsComplete);
        Assert.Equal("user@example.com", step.StatusDetail);
        Assert.Equal(0, claude.LoginStarts);
    });

    [Fact]
    public Task Login_after_a_few_checks_completes_asks_for_next_and_then_closes_the_handle() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        var nexts = 0;
        step.NextRequested += (_, _) => nexts++;
        await step.EnterAsync(CancellationToken.None);
        Assert.Equal(LoginState.NotLoggedIn, step.State);

        claude.Statuses.Enqueue(Out);
        claude.Statuses.Enqueue(Out);
        claude.Statuses.Enqueue(In);
        await step.StartAsync();
        Assert.Equal(LoginState.Waiting, step.State);
        Assert.True(step.IsPolling);

        clock.Tick();
        clock.Tick();
        Assert.Equal(LoginState.Waiting, step.State);
        Assert.Equal(0, nexts);
        Assert.Equal(0, claude.LastHandle!.Disposals);

        clock.Tick();
        await step.Polling;
        Assert.Equal(LoginState.LoggedIn, step.State);
        Assert.True(step.IsComplete);
        Assert.Equal(1, nexts);
        Assert.False(step.IsPolling);
        Assert.Equal(0, clock.Pending);
        Assert.Equal(1, claude.LastHandle!.Disposals);
    });

    [Fact]
    public Task No_browser_shows_the_address_and_copies_it() => HeadlessHost.Run(async () =>
    {
        var copied = new List<string>();
        var claude = new FakeClaude { LoginUrl = null, LateLoginUrl = "https://login.example.com/late" };
        using var step = new LoginStep(Context(claude, copied), new FakeClock().Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        Settle();
        Assert.True(step.ShowsNoBrowser);
        Assert.Equal("https://login.example.com/late", step.Url);

        await step.CopyUrlAsync();
        Assert.Equal(["https://login.example.com/late"], copied);
        Assert.Equal(LoginText.Copied(false), step.CopyNote);
    });

    [Fact]
    public Task Without_an_address_the_text_says_to_start_again() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { LoginUrl = null };
        using var step = new LoginStep(Context(claude), new FakeClock().Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        Assert.False(step.HasUrl);
        Assert.Equal(LoginText.NoBrowserNoUrl(false), step.NoBrowserText);
    });

    [Fact]
    public Task Status_not_supported_stops_polling_keeps_late_address_and_the_button_finishes() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { LoginUrl = null, LastStatus = new ClaudeLoginStatus(false, null, StatusSupported: false) };
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        Assert.True(step.ShowsDone);
        await step.StartAsync();
        clock.Tick();
        Assert.False(step.IsPolling);
        Assert.Equal(0, clock.Pending);
        Assert.False(step.IsComplete);

        // 주기가 멈춰도 로그인은 살아 있어 늦은 주소를 받는다.
        claude.LastUrlProgress!.Report("https://login.example.com/late");
        Settle();
        Assert.Equal("https://login.example.com/late", step.Url);

        await step.ConfirmDoneAsync();
        Assert.True(step.IsComplete);
    });

    [Fact]
    public Task Done_button_when_not_yet_logged_in_says_so() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        using var step = new LoginStep(Context(claude), new FakeClock().Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.ConfirmDoneAsync();
        Assert.False(step.IsComplete);
        Assert.Equal(LoginText.NotYet(false), step.StatusDetail);

        claude.LastStatus = In;
        await step.ConfirmDoneAsync();
        Assert.True(step.IsComplete);
    });

    [Fact]
    public Task Leaving_stops_polling_and_closes_the_login_process() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        var checks = claude.StatusChecks;

        await step.LeaveAsync(CancellationToken.None);
        Assert.False(step.IsPolling);
        Assert.Equal(1, claude.LastHandle!.Disposals);
        Assert.Equal(LoginState.NotLoggedIn, step.State);
        Assert.Equal(0, clock.Pending);

        claude.LastStatus = In;
        clock.Tick();
        Assert.Equal(checks, claude.StatusChecks);
        Assert.False(step.IsComplete);
    });

    [Fact]
    public Task Leaving_while_starting_closes_the_late_handle_and_does_not_poll() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { StartGate = new TaskCompletionSource() };
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        var starting = step.StartAsync();
        Assert.False(step.CanStart);

        await step.LeaveAsync(CancellationToken.None);
        claude.StartGate.SetResult();
        await starting;
        Settle();
        Assert.Equal(1, claude.LastHandle!.Disposals);
        Assert.Equal(0, clock.Pending);
        Assert.Equal(LoginState.NotLoggedIn, step.State);
        Assert.True(step.CanStart);
    });

    [Fact]
    public Task Start_twice_quickly_keeps_one_login_and_restart_closes_the_old_one() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { StartGate = new TaskCompletionSource() };
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        var first = step.StartAsync();
        await step.StartAsync();
        Assert.Equal(1, claude.LoginStarts);
        claude.StartGate.SetResult();
        await first;
        Assert.Single(claude.Handles);

        claude.StartGate = null;
        await step.StartAsync();
        Assert.Equal(2, claude.Handles.Count);
        Assert.Equal(1, claude.Handles[0].Disposals);
        Assert.Equal(0, claude.Handles[1].Disposals);
        Assert.Equal(1, clock.Pending);
    });

    [Fact]
    public Task Process_exit_without_login_closes_the_handle_and_asks_to_start_again() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { Exited = new TaskCompletionSource<int>() };
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        claude.Exited.SetResult(1);
        Settle();
        Assert.Equal(LoginState.NotLoggedIn, step.State);
        Assert.Equal(LoginText.Closed(false), step.StatusDetail);
        Assert.Equal(1, claude.LastHandle!.Disposals);
        Assert.Equal(0, clock.Pending);
        Assert.Equal(LoginText.StartAgain(false), step.StartText);
    });

    [Fact]
    public Task One_failed_check_does_not_stop_waiting() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        var clock = new FakeClock();
        using var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        claude.Statuses.Enqueue(new ClaudeLoginStatus(false, null, StatusSupported: true, AiSetupErrorKind.Unknown));
        claude.Statuses.Enqueue(In);
        await step.StartAsync();
        clock.Tick();
        Assert.Equal(LoginState.Waiting, step.State);
        clock.Tick();
        Assert.Equal(LoginState.LoggedIn, step.State);
    });

    [Fact]
    public Task Late_done_check_after_leaving_is_ignored() => HeadlessHost.Run(async () =>
    {
        var claude = new SlowStatusClaude();
        using var step = new LoginStep(Context(claude), new FakeClock().Delay);
        claude.Gate = null;
        await step.EnterAsync(CancellationToken.None);
        claude.Gate = new TaskCompletionSource();
        var confirming = step.ConfirmDoneAsync();
        await step.LeaveAsync(CancellationToken.None);
        claude.LastStatus = In;
        claude.Gate.SetResult();
        await confirming;
        Assert.False(step.IsComplete);
        Assert.Equal(LoginState.NotLoggedIn, step.State);
    });

    [Fact]
    public Task Start_failure_shows_plain_text() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { LoginStartFails = AiSetupErrorKind.CliNotFound };
        using var step = new LoginStep(Context(claude), new FakeClock().Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        Assert.Equal(LoginState.Failed, step.State);
        Assert.Equal(AiSetupErrorText.For(AiSetupErrorKind.CliNotFound, false), step.StatusDetail);
        Assert.True(step.CanStart);
    });

    [Fact]
    public Task Dispose_closes_the_handle() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        var clock = new FakeClock();
        var step = new LoginStep(Context(claude), clock.Delay);
        await step.EnterAsync(CancellationToken.None);
        await step.StartAsync();
        step.Dispose();
        Settle();
        Assert.Equal(1, claude.LastHandle!.Disposals);
        Assert.Equal(0, clock.Pending);
    });

    [Fact]
    public Task Wizard_moves_on_by_itself_after_login() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude();
        var clock = new FakeClock();
        var context = Context(claude);
        var factories = FakeStepFactory.All(AiSetupStepId.Login).Values.Cast<IAiSetupStepFactory>()
            .Append(new DelegateFactory(AiSetupStepId.Login, c => new LoginStep(c, clock.Delay)));
        using var vm = new AiSetupWizardViewModel(context, factories);
        await vm.StartAsync();
        context.ChosenPath = AiSetupPath.ClaudeAccount;
        ((FakeStep)vm.CurrentStep).IsComplete = true;
        await vm.NextAsync();
        ((FakeStep)vm.CurrentStep).IsComplete = true;
        await vm.NextAsync();
        var login = Assert.IsType<LoginStep>(vm.CurrentStep);
        await login.StartAsync();
        claude.LastStatus = In;
        clock.Tick();
        await vm.Idle;
        Settle();
        Assert.Equal(AiSetupStepId.Test, vm.CurrentStep.StepId);
        Assert.Equal(1, claude.LastHandle!.Disposals);
    });

    private sealed class DelegateFactory(AiSetupStepId id, Func<AiSetupContext, IAiSetupStep> create) : IAiSetupStepFactory
    {
        public AiSetupStepId StepId { get; } = id;

        public IAiSetupStep Create(AiSetupContext context) => create(context);
    }

    /// <summary>상태 확인이 늦게 돌아오는 가짜.</summary>
    private sealed class SlowStatusClaude : IClaudeSetupService
    {
        private readonly FakeClaude _inner = new();

        public TaskCompletionSource? Gate { get; set; }

        public ClaudeLoginStatus LastStatus { get; set; } = Out;

        public Task<ClaudeInstallation?> FindAsync(CancellationToken ct) => _inner.FindAsync(ct);

        public Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct) => _inner.InstallAsync(progress, ct);

        public Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct) => _inner.StartLoginAsync(browserUrl, ct);

        public async Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct)
        {
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return LastStatus;
        }

        public Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct) => _inner.TestConnectionAsync(ct);
    }
}
