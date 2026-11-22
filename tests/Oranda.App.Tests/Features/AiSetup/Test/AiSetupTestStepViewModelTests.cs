using Oranda.App.Features.AiSetup.Test;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.Test;

/// <summary>연결 시험 단계 모델(화면 없이): 갈래 A·B, 성공·실패 종류별 글, 다시 시험, 진단 글에 비밀 없음.</summary>
public sealed class AiSetupTestStepViewModelTests
{
    [Fact]
    public async Task Claude_success_shows_the_time_and_completes()
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(true, TimeSpan.FromMilliseconds(2800)));
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupTestState.Succeeded, model.State);
        Assert.True(model.IsComplete);
        Assert.Equal("AI 가 답했습니다 · 2.8초", model.Headline);
        Assert.Equal(AiSetupTestText.SucceededDetail(false), model.Detail);
        Assert.Null(model.Diagnostics);
    }

    [Theory]
    [InlineData(AiSetupErrorKind.NotLoggedIn)]
    [InlineData(AiSetupErrorKind.Network)]
    [InlineData(AiSetupErrorKind.Timeout)]
    [InlineData(AiSetupErrorKind.ServerRejected)]
    [InlineData(AiSetupErrorKind.CliNotFound)]
    [InlineData(AiSetupErrorKind.Unknown)]
    public async Task Claude_failure_shows_the_plain_cause_and_what_to_do(AiSetupErrorKind kind)
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(false, TimeSpan.FromSeconds(1), kind, "raw detail token=abc"));
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupTestState.Failed, model.State);
        Assert.False(model.IsComplete);
        Assert.Equal(kind, model.ErrorKind);
        Assert.Equal(AiSetupErrorText.For(kind, false), model.Headline);
        Assert.Equal(AiSetupTestText.WhatToDo(kind, AiSetupPath.ClaudeAccount, false), model.Detail);
        Assert.Contains($": {kind}", model.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("raw detail", model.Diagnostics, StringComparison.Ordinal); // 원문(Detail)은 진단 글에 넣지 않는다
    }

    [Fact]
    public async Task Retry_runs_again_and_can_turn_a_failure_into_success()
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(false, TimeSpan.FromSeconds(60), AiSetupErrorKind.Timeout));
        claude.Results.Enqueue(new AiSetupTestResult(true, TimeSpan.FromSeconds(3)));
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));
        var completeChanges = 0;
        model.PropertyChanged += (_, e) => completeChanges += e.PropertyName == nameof(AiSetupTestStepViewModel.IsComplete) ? 1 : 0;

        await model.RunAsync(CancellationToken.None);
        Assert.False(model.IsComplete);

        await model.RunAsync(CancellationToken.None);

        Assert.True(model.IsComplete);
        Assert.Equal(2, claude.Tests);
        Assert.Equal(1, completeChanges);
    }

    [Fact]
    public async Task Running_state_is_shown_while_waiting_and_a_second_run_is_ignored()
    {
        var claude = new FakeClaudeSetup { Gate = new TaskCompletionSource() };
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        var run = model.RunAsync(CancellationToken.None);
        Assert.Equal(AiSetupTestState.Running, model.State);
        Assert.Equal(AiSetupTestText.Running(false), model.Headline);
        Assert.False(model.CanRetry);
        await model.RunAsync(CancellationToken.None); // 도는 중에는 겹쳐 돌지 않는다

        claude.Gate.SetResult();
        await run;

        Assert.Equal(1, claude.Tests);
        Assert.Equal(AiSetupTestState.Succeeded, model.State);
    }

    [Fact]
    public async Task Cancel_stops_a_running_test_and_goes_back_to_idle()
    {
        var claude = new FakeClaudeSetup { Gate = new TaskCompletionSource() };
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        var run = model.RunAsync(CancellationToken.None);
        model.Cancel();
        await run;

        Assert.True(claude.Cancelled);
        Assert.Equal(AiSetupTestState.Idle, model.State);
        Assert.False(model.IsComplete);
    }

    [Fact]
    public async Task Running_again_while_a_cancelled_run_is_still_unwinding_starts_a_fresh_run()
    {
        // 떠났다가(멈춤) 곧바로 돌아왔는데 앞 시험이 아직 풀리는 중: 끝난 뒤 새로 돌아 빈 화면에 멈추지 않는다.
        var claude = new FakeClaudeSetup { Gate = new TaskCompletionSource(), SlowCancel = new TaskCompletionSource() };
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        var first = model.RunAsync(CancellationToken.None);
        model.Cancel();
        var second = model.RunAsync(CancellationToken.None);
        claude.Gate.SetResult();
        claude.SlowCancel.SetResult();
        await first;
        await second;

        Assert.Equal(2, claude.Tests);
        Assert.Equal(AiSetupTestState.Succeeded, model.State);
    }

    [Fact]
    public async Task An_unexpected_exception_ends_as_a_failure_not_a_spinner()
    {
        var claude = new FakeClaudeSetup { Throw = new InvalidOperationException("boom sk-secret-999") };
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude));

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupTestState.Failed, model.State);
        Assert.Equal(AiSetupErrorKind.Unknown, model.ErrorKind);
        Assert.True(model.CanRetry);
        Assert.DoesNotContain("sk-secret-999", model.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_claude_service_fails_with_a_notice()
    {
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude: null));

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupTestState.Failed, model.State);
        Assert.Equal(AiSetupTestText.Unavailable(false), model.Headline);
    }

    [Fact]
    public async Task Server_path_tests_the_created_profile_with_its_stored_key()
    {
        var probe = new FakeProbe(new AiConnectionTestResult(true, TimeSpan.FromSeconds(1.2), "서버가 응답했습니다(모델: big-model)."));
        var (context, _) = await AiSetupTestRig.ServerContext(probe);
        var model = new AiSetupTestStepViewModel(context);

        await model.RunAsync(CancellationToken.None);

        Assert.True(model.IsComplete);
        Assert.Equal("AI 가 답했습니다 · 1.2초", model.Headline);
        var request = Assert.Single(probe.Requests);
        Assert.Equal(AiSetupTestRig.Profile.Id, request.Profile.Id);
        Assert.Equal(AiSetupTestRig.SecretKey, request.ApiKey);
    }

    [Fact]
    public async Task Server_failure_is_sorted_and_the_diagnostics_hold_no_key_or_address()
    {
        var probe = new FakeProbe(new AiConnectionTestResult(false, TimeSpan.FromSeconds(2), "서버가 인증을 거부했습니다(401). sk-test-SECRET-123456 https://ai.example.com/v1"));
        var (context, _) = await AiSetupTestRig.ServerContext(probe);
        var model = new AiSetupTestStepViewModel(context);

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupErrorKind.ServerRejected, model.ErrorKind);
        Assert.Equal(AiSetupTestText.WhatToDo(AiSetupErrorKind.ServerRejected, AiSetupPath.CompanyServer, false), model.Detail);
        Assert.DoesNotContain(AiSetupTestRig.SecretKey, model.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("ai.example.com", model.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("SAMPLE-PROFILE", model.Diagnostics, StringComparison.Ordinal); // 프로필 이름도 없음
    }

    [Fact]
    public async Task Server_path_without_a_created_profile_asks_to_go_back()
    {
        var probe = new FakeProbe(new AiConnectionTestResult(true, TimeSpan.Zero));
        var (context, _) = await AiSetupTestRig.ServerContext(probe, withProfile: false);
        var model = new AiSetupTestStepViewModel(context);

        await model.RunAsync(CancellationToken.None);

        Assert.Equal(AiSetupTestState.Failed, model.State);
        Assert.Equal(AiSetupTestText.NoProfile(false), model.Headline);
        Assert.Empty(probe.Requests);
    }

    [Fact]
    public async Task Copy_puts_the_diagnostics_on_the_clipboard()
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(false, TimeSpan.FromSeconds(1), AiSetupErrorKind.Network));
        string? copied = null;
        var context = AiSetupTestRig.ClaudeContext(claude);
        var model = new AiSetupTestStepViewModel(
            new Oranda.App.Features.AiSetup.Contract.AiSetupContext(context.Settings)
            {
                Text = context.Text,
                Claude = claude,
                ChosenPath = AiSetupPath.ClaudeAccount,
                AppVersion = "0.1.5",
                CopyText = text =>
                {
                    copied = text;
                    return Task.CompletedTask;
                },
            },
            () => AiSetupTestRig.Now);

        await model.RunAsync(CancellationToken.None);
        await model.CopyAsync();

        Assert.Equal(model.Diagnostics, copied);
        Assert.Contains("2026-11-23 10:12:00 +09:00", copied, StringComparison.Ordinal);
        Assert.Contains("2.1.330", copied, StringComparison.Ordinal);
        Assert.True(model.JustCopied);
    }

    [Fact]
    public async Task English_texts_follow_the_context_language()
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(true, TimeSpan.FromSeconds(2)));
        var model = new AiSetupTestStepViewModel(AiSetupTestRig.ClaudeContext(claude, english: true));

        await model.RunAsync(CancellationToken.None);

        Assert.Equal("The AI answered · 2.0 s", model.Headline);
    }
}
