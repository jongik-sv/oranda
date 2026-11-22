using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.Login;

/// <summary>로그인 단계 등록.</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
public sealed class LoginStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Login;

    public IAiSetupStep Create(AiSetupContext context) => new LoginStep(context);
}

/// <summary>로그인 단계 상태.</summary>
public enum LoginState
{
    /// <summary>상태 확인 중.</summary>
    Checking,

    /// <summary>로그인 안 함(「로그인 창 열기」).</summary>
    NotLoggedIn,

    /// <summary>로그인 창을 열고 기다리는 중(몇 초마다 확인).</summary>
    Waiting,

    /// <summary>로그인됨(끝남).</summary>
    LoggedIn,

    /// <summary>로그인을 시작하지 못함·확인 실패(claude 없음 등).</summary>
    Failed,
}

/// <summary>
/// 로그인 단계(갈래 A): 들어오면 상태를 한 번 본다 — 이미 로그인 = 계정 + 끝남. 아니면 「로그인 창 열기」 → 서비스가 뒤에서 로그인을 시작(브라우저가 열림)하고
/// 몇 초(<see cref="PollInterval"/>)마다 상태를 본다. 로그인되면 계정을 보이고 자동으로 [다음]. 「인터넷 창이 안 열려요」 = 주소 + 복사.
/// 「로그인 끝났어요」 단추는 늘 있다(한 번 확인, 상태 확인을 지원하지 않는 판이면 그대로 끝남).
/// 떠나거나 창을 닫으면 확인 주기를 멈추고 로그인 프로세스 손잡이를 닫는다. 로그인을 확인한 뒤에도 손잡이를 닫는다(먼저 닫지 않음).
/// 로그인 한 번(시작~끝)을 세션 번호로 묶어, 떠난 뒤·다시 시작한 뒤 늦게 온 결과는 버린다. 화면 스레드에서만 쓴다.
/// </summary>
public sealed class LoginStep : AiSetupStepBase
{
    /// <summary>상태 확인 간격.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>확인 주기에서 일시 오류를 몇 번까지 참는가.</summary>
    private const int MaxPollErrors = 3;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private LoginState _state = LoginState.Checking;
    private string? _account;
    private string? _url;
    private string? _note;
    private string? _copyNote;
    private AiSetupErrorKind? _error;
    private CancellationTokenSource? _polling;
    private CancellationTokenSource? _session;
    private IAsyncDisposable? _handle;
    private int _sessionId;
    private bool _starting;
    private bool _confirming;
    private bool _started;

    /// <param name="delay">기다리기(시험은 가짜 시계). 기본 = <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public LoginStep(AiSetupContext context, Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(context)
    {
        _delay = delay ?? Task.Delay;
    }

    public override AiSetupStepId StepId => AiSetupStepId.Login;

    public LoginState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                IsComplete = value == LoginState.LoggedIn;
                RaiseButtons();
                RaiseStatus();
            }
        }
    }

    /// <summary>계정 글(화면에만). 모르면 null.</summary>
    public string? Account => _account;

    /// <summary>브라우저가 안 열릴 때 복사할 주소. 모르면 null.</summary>
    public string? Url
    {
        get => _url;
        private set
        {
            if (Set(ref _url, value))
            {
                OnPropertyChanged(nameof(HasUrl));
                OnPropertyChanged(nameof(NoBrowserText));
            }
        }
    }

    public bool HasUrl => !string.IsNullOrEmpty(_url);

    /// <summary>몇 초마다 확인하는 중.</summary>
    public bool IsPolling => _state == LoginState.Waiting && _polling is not null;

    /// <summary>「로그인 창 열기」 켜짐(시작하는 중에는 꺼짐: 두 번 눌러 브라우저가 둘 열리지 않게).</summary>
    public bool CanStart => Context.Claude is not null && !_starting && _state is LoginState.NotLoggedIn or LoginState.Waiting or LoginState.Failed;

    public string StartText => _started ? LoginText.StartAgain(English) : LoginText.Start(English);

    /// <summary>「로그인 끝났어요」 단추(로그인 전 언제든).</summary>
    public bool ShowsDone => Context.Claude is not null && _state != LoginState.LoggedIn && _state != LoginState.Checking;

    /// <summary>「인터넷 창이 안 열려요」 펼침(기다리는 중).</summary>
    public bool ShowsNoBrowser => _state == LoginState.Waiting;

    public string NoBrowserText => HasUrl ? LoginText.NoBrowserHow(English) : LoginText.NoBrowserNoUrl(English);

    public string? CopyNote
    {
        get => _copyNote;
        private set => Set(ref _copyNote, value);
    }

    public AiSetupStatusKind StatusKind => _state switch
    {
        LoginState.LoggedIn => AiSetupStatusKind.Ok,
        LoginState.Checking or LoginState.Waiting => AiSetupStatusKind.Wait,
        LoginState.Failed => AiSetupStatusKind.Bad,
        _ => AiSetupStatusKind.Idle,
    };

    public string StatusTitle => _state switch
    {
        LoginState.Checking => LoginText.Checking(English),
        LoginState.LoggedIn => LoginText.LoggedIn(English),
        LoginState.Waiting => LoginText.Waiting(English),
        LoginState.Failed => LoginText.StartFailed(English),
        _ => LoginText.NotLoggedIn(English),
    };

    public string? StatusDetail => _note ?? _state switch
    {
        LoginState.LoggedIn => _account ?? LoginText.LoggedInNoAccount(English),
        LoginState.Waiting => LoginText.WaitingDetail(English),
        LoginState.Failed => AiSetupErrorText.For(_error ?? AiSetupErrorKind.Unknown, English),
        LoginState.NotLoggedIn => LoginText.NotLoggedInDetail(English),
        _ => null,
    };

    /// <summary>지금 도는 확인 주기(시험이 기다린다). 없으면 끝난 일.</summary>
    internal Task Polling { get; private set; } = Task.CompletedTask;

    public override async Task EnterAsync(CancellationToken ct)
    {
        if (Context.Claude is null)
        {
            _error = AiSetupErrorKind.CliNotFound;
            State = LoginState.Failed;
            return;
        }

        if (_state is LoginState.Waiting && _polling is not null)
        {
            return;
        }

        var session = _sessionId;
        SetNote(null);
        State = LoginState.Checking;
        var status = await CheckAsync(ct).ConfigureAwait(true);
        if (IsDisposed || session != _sessionId || _state != LoginState.Checking)
        {
            return;
        }

        if (status is not null)
        {
            Apply(status, fromButton: false, autoNext: false);
        }

        if (_state == LoginState.Checking)
        {
            State = LoginState.NotLoggedIn;
        }
    }

    public override async Task LeaveAsync(CancellationToken ct)
    {
        EndSession();
        await CloseHandleAsync().ConfigureAwait(true);
        if (_state is LoginState.Waiting or LoginState.Checking)
        {
            State = LoginState.NotLoggedIn;
        }
    }

    /// <summary>「로그인 창 열기」: 하던 로그인을 끊고 새로 시작해 확인 주기를 돌린다. 시작하는 동안은 다시 누를 수 없다.</summary>
    public async Task StartAsync()
    {
        if (!CanStart || Context.Claude is not { } claude)
        {
            return;
        }

        // 세션 번호는 await 전에 잡는다: 옛 손잡이를 닫는 사이 떠나면 그 번호가 바뀌어 시작하지 않는다.
        var session = EndSession() + 1;
        _sessionId = session;
        var token = (_session = new CancellationTokenSource()).Token;
        _starting = true;
        RaiseButtons();
        try
        {
            await CloseHandleAsync().ConfigureAwait(true);
            if (session != _sessionId || IsDisposed)
            {
                return;
            }

            SetNote(null);
            Url = null;
            CopyNote = null;
            ClaudeLoginStart start;
            try
            {
                start = await claude.StartLoginAsync(new UrlProgress(this, SynchronizationContext.Current, session), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                start = new ClaudeLoginStart(false, null, null, AiSetupErrorKind.Unknown);
            }

            if (session != _sessionId || IsDisposed)
            {
                // 기다리는 사이 단계를 떠났다·다시 시작했다: 막 띄운 로그인은 끊는다.
                if (start.Handle is { } late)
                {
                    await DisposeQuietlyAsync(late).ConfigureAwait(true);
                }

                return;
            }

            _started = true;
            if (!start.Started)
            {
                _error = start.ErrorKind ?? AiSetupErrorKind.Unknown;
                State = LoginState.Failed;
                RaiseStatus();
                return;
            }

            _handle = start.Handle;
            if (start.BrowserUrl is { Length: > 0 } url)
            {
                Url = url;
            }

            var polling = new CancellationTokenSource();
            _polling = polling;
            State = LoginState.Waiting;
            if (start.Exited is { } exited)
            {
                _ = WatchExitAsync(exited, session, token);
            }

            Polling = PollAsync(session, polling.Token);
        }
        finally
        {
            if (session == _sessionId)
            {
                _starting = false;
            }

            RaiseButtons();
        }
    }

    /// <summary>「로그인 끝났어요」: 한 번 확인. 상태 확인을 지원하지 않으면 그대로 끝남. 누르는 사이 떠나면 결과를 버린다.</summary>
    public async Task ConfirmDoneAsync()
    {
        if (Context.Claude is null || _state == LoginState.LoggedIn || _confirming)
        {
            return;
        }

        _confirming = true;
        var session = _sessionId;
        try
        {
            var status = await CheckAsync(CancellationToken.None).ConfigureAwait(true);
            if (status is null || IsDisposed || session != _sessionId || _state == LoginState.LoggedIn)
            {
                return;
            }

            Apply(status, fromButton: true, autoNext: true);
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>주소 복사.</summary>
    public async Task CopyUrlAsync()
    {
        if (_url is not { Length: > 0 } url)
        {
            return;
        }

        try
        {
            await Context.CopyText(url).ConfigureAwait(true);
            CopyNote = LoginText.Copied(English);
        }
        catch (Exception)
        {
            CopyNote = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        EndSession();
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            _ = DisposeQuietlyAsync(handle);
        }

        base.Dispose(disposing);
    }

    protected override Control BuildView()
    {
        var start = AiSetupUi.Button(LoginText.Start(English), accent: true);
        start.Bind(ContentControl.ContentProperty, new Binding(nameof(StartText)));
        start.Bind(Visual.IsVisibleProperty, new Binding(nameof(ShowsStart)));
        start.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(nameof(CanStart)));
        start.Click += async (_, _) => await StartAsync().ConfigureAwait(true);
        StartButton = start;

        var done = AiSetupUi.Button(LoginText.Done(English));
        done.Bind(Visual.IsVisibleProperty, new Binding(nameof(ShowsDone)));
        done.Click += async (_, _) => await ConfirmDoneAsync().ConfigureAwait(true);
        DoneButton = done;

        var how = AiSetupUi.Text(string.Empty);
        how.Bind(TextBlock.TextProperty, new Binding(nameof(NoBrowserText)));
        var line = AiSetupUi.CopyLine(nameof(Url), LoginText.Copy(English), () => _ = CopyUrlAsync());
        line.Bind(Visual.IsVisibleProperty, new Binding(nameof(HasUrl)));
        var copyNote = AiSetupUi.Caption(string.Empty);
        copyNote.Bind(TextBlock.TextProperty, new Binding(nameof(CopyNote)));
        copyNote.Bind(Visual.IsVisibleProperty, new Binding(nameof(CopyNote)) { Converter = StringConverters.IsNotNullOrEmpty });
        var noBrowser = new Expander
        {
            Header = LoginText.NoBrowser(English),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { how, line, copyNote } },
        };
        noBrowser.Bind(Visual.IsVisibleProperty, new Binding(nameof(ShowsNoBrowser)));

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                AiSetupUi.Caption(LoginText.Intro(English)),
                AiSetupUi.Status(nameof(StatusKind), nameof(StatusTitle), nameof(StatusDetail)),
                new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { start, done } },
                noBrowser,
            },
        };
    }

    /// <summary>「로그인 창 열기」 단추를 보인다(로그인됨·확인 중이 아닐 때).</summary>
    public bool ShowsStart => Context.Claude is not null && _state is LoginState.NotLoggedIn or LoginState.Waiting or LoginState.Failed;

    internal Button? StartButton { get; private set; }

    internal Button? DoneButton { get; private set; }

    private async Task PollAsync(int session, CancellationToken ct)
    {
        var errors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(PollInterval, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || IsDisposed || session != _sessionId)
            {
                return;
            }

            var status = await CheckAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || IsDisposed || session != _sessionId || _state != LoginState.Waiting)
            {
                return;
            }

            if (status is null)
            {
                continue;
            }

            if (status.LoggedIn)
            {
                Apply(status, fromButton: false, autoNext: true);
                return;
            }

            if (status.ErrorKind is { } kind && kind != AiSetupErrorKind.NotLoggedIn)
            {
                // 한 번의 확인 실패(잠깐 느림 등)로 멈추지 않는다. 여러 번 이어지면 실패로 보인다.
                if (++errors < MaxPollErrors)
                {
                    continue;
                }

                Apply(status, fromButton: false, autoNext: false);
                return;
            }

            errors = 0;
            if (!status.StatusSupported)
            {
                // 상태 확인을 못 하는 판: 주기만 멈추고(로그인은 그대로) 「로그인 끝났어요」 단추에 맡긴다.
                StopPolling();
                return;
            }
        }
    }

    private async Task WatchExitAsync(Task<int> exited, int session, CancellationToken ct)
    {
        try
        {
            await exited.WaitAsync(ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        if (ct.IsCancellationRequested || IsDisposed || session != _sessionId || _state != LoginState.Waiting)
        {
            return;
        }

        // 로그인 프로세스가 끝났다(끝남 ≠ 로그인됨): 한 번 더 본다.
        var status = await CheckAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested || IsDisposed || session != _sessionId || status is null || _state != LoginState.Waiting)
        {
            return;
        }

        if (status.LoggedIn)
        {
            Apply(status, fromButton: false, autoNext: true);
            return;
        }

        if (status.StatusSupported)
        {
            EndSession();
            await CloseHandleAsync().ConfigureAwait(true);
            if (IsDisposed || _state != LoginState.Waiting)
            {
                return;
            }

            State = LoginState.NotLoggedIn;
            SetNote(LoginText.Closed(English));
        }
    }

    private async Task<ClaudeLoginStatus?> CheckAsync(CancellationToken ct)
    {
        if (Context.Claude is not { } claude)
        {
            return null;
        }

        try
        {
            return await claude.GetLoginStatusAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return new ClaudeLoginStatus(false, null, StatusSupported: true, AiSetupErrorKind.Unknown);
        }
    }

    private void Apply(ClaudeLoginStatus status, bool fromButton, bool autoNext)
    {
        if (status.LoggedIn || (fromButton && !status.StatusSupported && status.ErrorKind is null))
        {
            // 로그인을 확인한 뒤에 손잡이를 닫는다(먼저 닫으면 브라우저 쪽이 같이 끊길 수 있음).
            EndSession();
            _ = CloseHandleAsync();
            _account = status.AccountLabel;
            OnPropertyChanged(nameof(Account));
            SetNote(null);
            State = LoginState.LoggedIn;
            RaiseStatus();
            if (autoNext)
            {
                RequestNext();
            }

            return;
        }

        if (status.ErrorKind is { } kind && kind != AiSetupErrorKind.NotLoggedIn)
        {
            EndSession();
            _ = CloseHandleAsync();
            _error = kind;
            State = LoginState.Failed;
            RaiseStatus();
            return;
        }

        if (fromButton)
        {
            SetNote(LoginText.NotYet(English));
        }
        else if (_state == LoginState.Checking)
        {
            State = LoginState.NotLoggedIn;
        }
    }

    /// <summary>지금 로그인 세션을 끝낸다(시작 취소·확인 주기 멈춤). 늦게 온 결과는 이 번호로 버린다. 끝낸 세션 번호를 돌려준다.</summary>
    private int EndSession()
    {
        _sessionId++;
        _starting = false;
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            session.Cancel();
            session.Dispose();
        }

        StopPolling();
        RaiseButtons();
        return _sessionId;
    }

    /// <summary>확인 주기만 멈춘다(로그인 프로세스·늦은 주소는 그대로).</summary>
    private void StopPolling()
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling is not null)
        {
            polling.Cancel();
            polling.Dispose();
            OnPropertyChanged(nameof(IsPolling));
        }
    }

    private async Task CloseHandleAsync()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            await DisposeQuietlyAsync(handle).ConfigureAwait(true);
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable handle)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 이미 끝난 프로세스 등.
        }
    }

    private void SetNote(string? note)
    {
        if (_note != note)
        {
            _note = note;
            OnPropertyChanged(nameof(StatusDetail));
        }
    }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(IsPolling));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowsStart));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(ShowsDone));
        OnPropertyChanged(nameof(ShowsNoBrowser));
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
    }

    private void SetLateUrl(string url, int session)
    {
        if (session == _sessionId && !IsDisposed && !string.IsNullOrWhiteSpace(url))
        {
            Url = url.Trim();
        }
    }

    /// <summary>늦게 온 로그인 주소를 화면 스레드로 옮긴다.</summary>
    private sealed class UrlProgress(LoginStep owner, SynchronizationContext? context, int session) : IProgress<string>
    {
        public void Report(string value)
        {
            if (context is null || SynchronizationContext.Current == context)
            {
                owner.SetLateUrl(value, session);
            }
            else
            {
                context.Post(_ => owner.SetLateUrl(value, session), null);
            }
        }
    }
}
