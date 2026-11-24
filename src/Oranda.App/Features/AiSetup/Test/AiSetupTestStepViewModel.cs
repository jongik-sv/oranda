using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;

namespace Oranda.App.Features.AiSetup.Test;

/// <summary>연결 시험 단계의 상태.</summary>
public enum AiSetupTestState
{
    /// <summary>아직 안 함(또는 멈춤).</summary>
    Idle,

    Running,

    Succeeded,

    Failed,
}

/// <summary>
/// 연결 시험 단계 모델(화면 없음). 갈래 A = <see cref="IClaudeSetupService.TestConnectionAsync"/>,
/// 갈래 B = 회사 AI 서버 단계가 만든 프로필로 <see cref="IAiProfileProbe.TestConnectionAsync"/>. DB 는 건드리지 않는다.
/// 화면 스레드에서만 쓴다.
/// </summary>
public sealed class AiSetupTestStepViewModel : INotifyPropertyChanged
{
    private readonly AiSetupContext _context;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _running;
    private Task _current = Task.CompletedTask;
    private AiSetupTestState _state;
    private string _headline = "";
    private string _detail = "";
    private string? _diagnostics;
    private AiSetupErrorKind? _errorKind;
    private bool _justCopied;

    /// <param name="clock">진단 글 시각(시험용). 없으면 지금.</param>
    public AiSetupTestStepViewModel(AiSetupContext context, Func<DateTimeOffset>? clock = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool English => _context.IsEnglish;

    public AiSetupTestState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            var wasComplete = IsComplete;
            _state = value;
            Raise();
            Raise(nameof(CanRetry));
            Raise(nameof(StatusKind));
            Raise(nameof(IsRunning));
            Raise(nameof(IsFailed));
            if (wasComplete != IsComplete)
            {
                Raise(nameof(IsComplete));
            }
        }
    }

    /// <summary>상태 칸 모양(창 틀 <see cref="AiSetupUi.Status"/>).</summary>
    public AiSetupStatusKind StatusKind => _state switch
    {
        AiSetupTestState.Succeeded => AiSetupStatusKind.Ok,
        AiSetupTestState.Failed => AiSetupStatusKind.Bad,
        AiSetupTestState.Running => AiSetupStatusKind.Wait,
        _ => AiSetupStatusKind.Idle,
    };

    /// <summary>묻는 중(진행 막대).</summary>
    public bool IsRunning => _state == AiSetupTestState.Running;

    /// <summary>실패(「다시 시험」 보임).</summary>
    public bool IsFailed => _state == AiSetupTestState.Failed;

    /// <summary>시험이 성공했다([다음] 켜짐).</summary>
    public bool IsComplete => _state == AiSetupTestState.Succeeded;

    /// <summary>「다시 시험」 을 누를 수 있다(도는 중이 아님).</summary>
    public bool CanRetry => _state != AiSetupTestState.Running;

    /// <summary>상태 머리 한 줄(묻는 중·답 시간·쉬운 원인).</summary>
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    /// <summary>머리 아래 한 줄(다음 할 일).</summary>
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>실패 때 복사할 진단 글(비밀 없음). 성공·도는 중이면 null.</summary>
    public string? Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }

    /// <summary>실패 종류. 성공·도는 중이면 null.</summary>
    public AiSetupErrorKind? ErrorKind { get => _errorKind; private set => Set(ref _errorKind, value); }

    /// <summary>방금 진단 글을 복사했다(단추 옆 「복사했습니다」).</summary>
    public bool JustCopied { get => _justCopied; private set => Set(ref _justCopied, value); }

    /// <summary>
    /// 시험을 한 번 돌린다. 도는 중이면 아무것도 안 한다. 멈추라고 한 시험이 아직 풀리는 중이면(떠났다가 곧바로 돌아옴)
    /// 그것이 끝나기를 기다린 뒤 새로 돈다. 실패는 화면 값으로 남고 예외로 내지 않는다.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        if (!_current.IsCompleted && _running is { IsCancellationRequested: false })
        {
            return Task.CompletedTask;
        }

        _current = RunAfterAsync(_current, ct);
        return _current;
    }

    private async Task RunAfterAsync(Task previous, CancellationToken ct)
    {
        await previous.ConfigureAwait(true); // 앞 실행은 예외를 내지 않는다(아래에서 모두 잡음)
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = running;
        JustCopied = false;
        Diagnostics = null;
        ErrorKind = null;
        Headline = AiSetupTestText.Running(English);
        Detail = "";
        State = AiSetupTestState.Running;
        try
        {
            var path = _context.ChosenPath;
            var outcome = path == AiSetupPath.CompanyServer
                ? await TestServerAsync(running.Token).ConfigureAwait(true)
                : await TestClaudeAsync(running.Token).ConfigureAwait(true);

            if (outcome.Notice is null)
            {
                await RecordAsync(_context.Settings, Record(outcome, path)).ConfigureAwait(true); // 시험을 실제로 한 경우만 남긴다(설정 탭 「연결 상태」 카드)
            }

            if (outcome.Success)
            {
                Headline = AiSetupTestText.Succeeded(outcome.Elapsed, English);
                Detail = AiSetupTestText.SucceededDetail(English);
                State = AiSetupTestState.Succeeded;
                return;
            }

            var kind = outcome.Kind ?? AiSetupErrorKind.Unknown;
            ErrorKind = kind;
            Headline = outcome.Notice ?? AiSetupErrorText.For(kind, English);
            Detail = outcome.Notice is null ? AiSetupTestText.WhatToDo(kind, path, English) : "";
            Diagnostics = await BuildDiagnosticsAsync(kind, running.Token).ConfigureAwait(true);
            State = AiSetupTestState.Failed;
        }
        catch (OperationCanceledException)
        {
            // 단계를 떠났거나 창을 닫았다. 다음에 들어오면 다시 시험한다.
            Headline = "";
            Detail = "";
            State = AiSetupTestState.Idle;
        }
        catch (Exception)
        {
            // 서비스는 실패를 결과로 돌려주지만, 뜻밖의 예외가 나도 화면이 「묻는 중」 에 멈추지 않게 실패로 보인다.
            ErrorKind = AiSetupErrorKind.Unknown;
            Headline = AiSetupErrorText.For(AiSetupErrorKind.Unknown, English);
            Detail = AiSetupTestText.WhatToDo(AiSetupErrorKind.Unknown, _context.ChosenPath, English);
            Diagnostics = AiSetupDiagnostics.ForTestFailure(
                new AiSetupDiagnosticInfo(_context.AppVersion, RuntimeInformation.OSDescription, _context.ChosenPath, false, null, AiSetupErrorKind.Unknown, _clock()),
                English);
            State = AiSetupTestState.Failed;
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }
        }
    }

    /// <summary>도는 시험을 멈춘다(단계를 떠날 때). 여러 번 불려도 된다.</summary>
    public void Cancel()
    {
        try
        {
            _running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 막 끝났다.
        }
    }

    /// <summary>진단 글을 클립보드로.</summary>
    public async Task CopyAsync()
    {
        if (Diagnostics is not { } text)
        {
            return;
        }

        await _context.CopyText(text).ConfigureAwait(true);
        JustCopied = true;
    }

    /// <summary>시험한 대상의 이름: 갈래 B 는 만든 프로필 이름, 그 밖은 Claude Code.</summary>
    private string TargetName(AiSetupPath? path) =>
        path == AiSetupPath.CompanyServer && _context.CreatedProfileId is { } id
        && _context.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id) is { } profile
            ? profile.Name
            : "Claude Code";

    private AiConnectionTestRecord Record(Outcome outcome, AiSetupPath? path) =>
        new(_clock(), outcome.Success, TargetName(path), (long)outcome.Elapsed.TotalMilliseconds, outcome.Success ? null : outcome.Kind ?? AiSetupErrorKind.Unknown);

    /// <summary>
    /// 마지막 연결 시험을 설정(<c>ai.setup.lastTest</c>)에 남긴다. 설정을 쓸 수 없으면(읽기 전용 등) 조용히 넘어간다 — 시험 결과 화면은 그대로다.
    /// 도우미의 연결 시험 단계와 설정 「AI」 탭의 「연결 시험」 이 함께 쓴다.
    /// </summary>
    internal static async Task RecordAsync(Oranda.App.Shell.Settings.SettingsService settings, AiConnectionTestRecord record)
    {
        try
        {
            await settings.UpdateAsync(s => s with { Ai = s.Ai with { Setup = s.Ai.Setup with { LastTest = record } } }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 기록만 못 한다.
        }
    }

    private async Task<Outcome> TestClaudeAsync(CancellationToken ct)
    {
        if (_context.Claude is not { } claude)
        {
            return Outcome.Fail(AiSetupErrorKind.Unknown, AiSetupTestText.Unavailable(English));
        }

        var result = await claude.TestConnectionAsync(ct).ConfigureAwait(true);
        return new Outcome(result.Success, result.Elapsed, result.Success ? null : result.ErrorKind ?? AiSetupErrorKind.Unknown, null);
    }

    private async Task<Outcome> TestServerAsync(CancellationToken ct)
    {
        if (_context.Probe is not { } probe)
        {
            return Outcome.Fail(AiSetupErrorKind.Unknown, AiSetupTestText.Unavailable(English));
        }

        var profile = _context.CreatedProfileId is { } id
            ? _context.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id)
            : null;
        if (profile is null)
        {
            return Outcome.Fail(AiSetupErrorKind.Unknown, AiSetupTestText.NoProfile(English));
        }

        var key = await LoadKeyAsync(profile).ConfigureAwait(true);
        var result = await probe.TestConnectionAsync(new AiProfileProbeRequest(profile, key), ct).ConfigureAwait(true);
        return new Outcome(result.Success, result.Elapsed, AiSetupErrorCodes.KindFor(result), null);
    }

    private async Task<string?> LoadKeyAsync(AiLlmProfile profile)
    {
        if (_context.Credentials is not { } store)
        {
            return null;
        }

        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(profile.Id)).ConfigureAwait(true);
            if (bytes is null)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // 열쇠 없이 시험한다(서버가 거절하면 거절로 보인다).
        }
    }

    private async Task<string> BuildDiagnosticsAsync(AiSetupErrorKind kind, CancellationToken ct)
    {
        ClaudeInstallation? claude = null;
        if (_context.Claude is { } service)
        {
            try
            {
                claude = await service.FindAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 진단 글에 「설치 안 됨」 으로 보인다.
            }
        }

        var info = new AiSetupDiagnosticInfo(
            _context.AppVersion,
            RuntimeInformation.OSDescription,
            _context.ChosenPath,
            claude is not null,
            claude?.Version,
            kind,
            _clock());
        return AiSetupDiagnostics.ForTestFailure(info, English);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <param name="Notice">원인 대신 보일 안내(시험을 하지 못한 경우).</param>
    private sealed record Outcome(bool Success, TimeSpan Elapsed, AiSetupErrorKind? Kind, string? Notice)
    {
        public static Outcome Fail(AiSetupErrorKind kind, string notice) => new(false, TimeSpan.Zero, kind, notice);
    }
}
