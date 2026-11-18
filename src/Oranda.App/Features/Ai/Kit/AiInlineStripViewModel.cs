using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>띠 한 번의 요청: 경로·꾸러미·지시 글·답 틀. 보낼 글은 <see cref="Prompt"/> 하나뿐이다(경고 창·이어 보기가 같은 글을 쓴다).</summary>
/// <param name="State">받아들이기에 되돌려 받을 기능 전용 값(대상 문장·탭 등). 띠는 열어 보지 않는다.</param>
public sealed record AiStripRequest(AiOneShotRun Run, IAiContextBundle Bundle, string Instruction, AiAnswerTemplate? Template = null, object? State = null)
{
    /// <summary>보낼 글: 지시 → 답 틀 → 꾸러미.</summary>
    public string Prompt
    {
        get
        {
            var parts = new List<string> { Instruction.Trim() };
            if (Template is { } template)
            {
                parts.Add(template.Rules);
            }

            parts.Add(Bundle.ToPrompt());
            return string.Join("\n\n", parts);
        }
    }
}

/// <summary>「받아들이기」 가 받는 값: 보낸 요청·받은 결과·틀대로 읽은 답.</summary>
public sealed record AiStripAccept(AiStripRequest Request, AiOneShotResult Result, AiTemplatedAnswer? Answer);

/// <summary>띠 요청 준비의 결과: 보낼 요청 또는 보내지 않는 까닭.</summary>
public sealed class AiStripPreparation
{
    private AiStripPreparation(AiStripRequest? request, string? refusal)
    {
        Request = request;
        RefusalText = refusal;
    }

    public AiStripRequest? Request { get; }

    public string? RefusalText { get; }

    public static AiStripPreparation Ready(AiStripRequest request) => new(request ?? throw new ArgumentNullException(nameof(request)), null);

    public static AiStripPreparation Refuse(string text) =>
        new(null, string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("까닭이 비었습니다.", nameof(text)) : text);
}

/// <summary>띠 상태.</summary>
public enum AiStripState
{
    /// <summary>아직 묻지 않음(띠를 숨긴다).</summary>
    Idle,

    /// <summary>준비·보내는 중.</summary>
    Running,

    /// <summary>답을 받음.</summary>
    Done,

    /// <summary>오류로 끝남.</summary>
    Failed,

    /// <summary>멈춤.</summary>
    Stopped,

    /// <summary>보내지 않음(준비가 거부·경고 창 취소).</summary>
    Refused,
}

/// <summary>
/// AI 띠(바로 처리, 설계 §2.1): 화면에 붙는 작은 결과 영역. 띠 하나 = 요청 하나. 진행 중에는 「멈춤」, 끝나면 「다시」·「복사」·「채팅에서 이어 보기」.
/// 답한 모델 이름을 작게 적는다. 「채팅에서 이어 보기」 는 보낸 글과 받은 답을 채팅창 새 대화의 첫 턴으로 옮기고(다시 보내지 않는다), 이어 묻기는 같은 요청 경로로 간다.
/// 보내기 전 확인(<see cref="Confirm"/>)·가림·받는 곳 확인·감사는 채팅창 쪽과 같은 경로(<see cref="AiOneShotRun"/>)를 거친다. AI 는 실행하지 않는다.
/// </summary>
public sealed partial class AiInlineStripViewModel : ObservableObject
{
    private readonly Func<AiTier?, CancellationToken, Task<AiStripPreparation>> _prepare;
    private readonly AiChatHost? _chatHost;
    private readonly bool _canRetryOnMain;

    /// <param name="title">띠 이름(「상황 풀이」).</param>
    /// <param name="prepare">
    /// 요청을 모은다(누를 때마다 새로 — 화면의 지금 상태로). 등급 인자 = null 이면 기능의 등급, <see cref="AiTier.Main"/> 이면 「주 모델로 다시 보내기」:
    /// 그 등급으로 <see cref="AiKitRoute.For"/> 를 다시 만들어야 한다(받는 곳·가림·확인이 바뀐 대상으로 다시 계산된다).
    /// </param>
    /// <param name="chatHost">채팅창 동작. null 이면 「채팅에서 이어 보기」 를 숨긴다.</param>
    public AiInlineStripViewModel(string title, Func<AiTier?, CancellationToken, Task<AiStripPreparation>> prepare, AiChatHost? chatHost = null)
        : this(title, prepare, chatHost, canRetryOnMain: true)
    {
    }

    /// <summary>등급을 바꿀 수 없는 띠(주 모델 기능 등). 「주 모델로 다시 보내기」 를 붙이지 않는다.</summary>
    public AiInlineStripViewModel(string title, Func<CancellationToken, Task<AiStripPreparation>> prepare, AiChatHost? chatHost = null)
        : this(title, prepare is null ? null! : (_, ct) => prepare(ct), chatHost, canRetryOnMain: false)
    {
    }

    private AiInlineStripViewModel(string title, Func<AiTier?, CancellationToken, Task<AiStripPreparation>> prepare, AiChatHost? chatHost, bool canRetryOnMain)
    {
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("띠 이름이 비었습니다.", nameof(title)) : title;
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _chatHost = chatHost;
        _canRetryOnMain = canRetryOnMain;
    }

    /// <summary>준비하다 예외가 났을 때.</summary>
    public const string PrepareFailedText = "보낼 정보를 모으지 못했습니다. 자세한 원인은 로그에 남겼습니다.";

    /// <summary>경고 창에서 취소했을 때.</summary>
    public const string CancelledText = "보내지 않았습니다.";

    private CancellationTokenSource? _cts;
    private DateTime _startedUtc;

    /// <summary>크기 올림 안내(설계 §1.2).</summary>
    public const string EscalatedText = Oranda.Core.Ai.Profiles.AiTierRouting.EscalatedText;

    public string Title { get; }

    /// <summary>보내기 직전 확인(경고 창: 받는 곳·꾸러미 미리 보기). null 이면 묻지 않는다. 거짓이면 보내지 않는다.</summary>
    public Func<AiStripRequest, CancellationToken, Task<bool>>? Confirm { get; init; }

    /// <summary>
    /// 「받아들이기」(편집기 제안 막대): 답을 편집기에 제안으로 넣는다(기존 차이 미리보기 경로). 넣었으면 null, 못 넣었으면 까닭. null 이면 단추를 숨긴다.
    /// 틀대로 읽은 답이 있을 때만 켜진다. AI 는 실행하지 않는다 — 넣은 글을 실행하는 일은 사람이 한다.
    /// </summary>
    public Func<AiStripAccept, CancellationToken, Task<string?>>? Accept { get; init; }

    /// <summary>받아들인 뒤 상태 줄 글.</summary>
    public const string AcceptedText = "편집기에 넣었습니다. 실행은 직접 하세요.";

    /// <summary>클립보드에 쓴다. 기본 = 주 창의 클립보드.</summary>
    public Func<string, Task> CopyText { get; init; } = DefaultCopyAsync;

    /// <summary>시계(시험이 바꾼다).</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsVisible), nameof(HasAnswer), nameof(IsDone), nameof(IsFailed), nameof(ShowsRawText), nameof(HasParts), nameof(CanContinueVisible), nameof(ShowsAccept))]
    [NotifyCanExecuteChangedFor(nameof(RequestCommand), nameof(StopCommand), nameof(RetryCommand), nameof(CopyCommand), nameof(ContinueInChatCommand), nameof(RetryOnMainCommand), nameof(AcceptCommand), nameof(DiscardCommand))]
    private AiStripState _state;

    /// <summary>받아들였다(같은 답을 두 번 넣지 않는다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAccept))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _isAccepted;

    private bool _accepting;

    /// <summary>「주 모델로 다시 보내기」 를 보인다(가벼운 일 모델이 실패했을 때만, AE-1).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryOnMainCommand))]
    private bool _showsRetryOnMain;

    /// <summary>받은 글(흘러오는 동안에도 쌓인다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _answerText = string.Empty;

    /// <summary>틀대로 읽은 답(틀이 없거나 깨졌으면 null — <see cref="AnswerText"/> 를 그대로 보인다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRawText), nameof(HasParts), nameof(Parts))]
    private AiTemplatedAnswer? _answer;

    /// <summary>답한 모델 이름.</summary>
    [ObservableProperty]
    private string _modelLabel = string.Empty;

    /// <summary>상태 한 줄(진행·오류·멈춤·거부 까닭).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>마지막으로 보낸 요청(이어 보기·시험).</summary>
    public AiStripRequest? LastRequest { get; private set; }

    /// <summary>마지막 결과.</summary>
    public AiOneShotResult? LastResult { get; private set; }

    /// <summary>「채팅에서 이어 보기」 로 연 대화(시험).</summary>
    public AiCarriedConversation? Carried { get; private set; }

    public bool IsRunning => State == AiStripState.Running;

    public bool IsVisible => State != AiStripState.Idle;

    public bool IsDone => State == AiStripState.Done;

    public bool IsFailed => State == AiStripState.Failed;

    public bool HasAnswer => AnswerText.Length > 0;

    public bool HasParts => Answer is not null && State == AiStripState.Done;

    public bool ShowsRawText => !HasParts && HasAnswer;

    public IReadOnlyList<AiAnswerPart> Parts => Answer?.Parts ?? [];

    public bool CanContinueVisible => _chatHost is not null && State == AiStripState.Done;

    /// <summary>「받아들이기」·「버리기」 를 보인다(제안 막대: 답을 틀대로 읽었고 아직 넣지 않음).</summary>
    public bool ShowsAccept => Accept is not null && State == AiStripState.Done && Answer is not null && !IsAccepted;

    private bool CanAccept() => ShowsAccept && !_accepting && LastRequest is not null && LastResult is not null;

    /// <summary>「받아들이기」: 기능이 편집기에 제안으로 넣는다(차이 미리보기에서 사람이 적용).</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task AcceptAsync()
    {
        if (Accept is not { } accept || LastRequest is not { } request || LastResult is not { } result)
        {
            return;
        }

        _accepting = true;
        AcceptCommand.NotifyCanExecuteChanged();
        try
        {
            var failure = await accept(new AiStripAccept(request, result, Answer), CancellationToken.None).ConfigureAwait(true);
            if (failure is null)
            {
                IsAccepted = true;
                StatusText = AcceptedText;
            }
            else
            {
                StatusText = failure;
            }
        }
        finally
        {
            _accepting = false;
            AcceptCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDiscard() => State is not AiStripState.Idle and not AiStripState.Running;

    /// <summary>「버리기」: 띠를 닫는다(편집기는 그대로).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        StatusText = string.Empty;
        State = AiStripState.Idle;
    }

    /// <summary>진행 줄: 「답을 받는 중 · 3초」.</summary>
    public void RefreshProgress()
    {
        if (IsRunning)
        {
            var seconds = Math.Max(0, (int)(UtcNow() - _startedUtc).TotalSeconds);
            StatusText = string.Create(CultureInfo.CurrentCulture, $"답을 받는 중 · {seconds}초");
        }
    }

    private bool CanRequest() => !IsRunning;

    /// <summary>「AI 풀이」 등 입구 단추가 부른다. 진행 중이면 무시한다.</summary>
    [RelayCommand(CanExecute = nameof(CanRequest))]
    private Task RequestAsync() => RunAsync(CancellationToken.None);

    private bool CanRetryOnMain() => ShowsRetryOnMain && !IsRunning;

    /// <summary>「주 모델로 다시 보내기」(AE-1): 등급을 주 모델로 바꾼 새 요청으로 다시 모으고 확인을 다시 거친다.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryOnMain))]
    private Task RetryOnMainAsync() => RunAsync(AiTier.Main, CancellationToken.None);

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private bool CanRetry() => State is AiStripState.Done or AiStripState.Failed or AiStripState.Stopped or AiStripState.Refused;

    /// <summary>「다시」: 화면의 지금 상태로 다시 모아 다시 보낸다.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => RunAsync(CancellationToken.None);

    private bool CanCopy() => HasAnswer && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopyAsync() => CopyText(AnswerText);

    private bool CanContinue() => CanContinueVisible && LastRequest is not null && LastResult is { Succeeded: true };

    /// <summary>「채팅에서 이어 보기」: 보낸 글과 받은 답을 채팅창 새 대화의 첫 턴으로(다시 보내지 않는다).</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueInChatAsync()
    {
        if (_chatHost is not { } host || LastRequest is not { } request || LastResult is not { Succeeded: true } result)
        {
            return;
        }

        var carried = await AiChatOpener.OpenAsync(host, chat => new AiCarriedConversation(chat, Title, request.Run, request.Prompt, result.Text, result.ResumeId)
        {
            ConfirmText = host.ConfirmText,
        }).ConfigureAwait(true);
        if (carried is not null)
        {
            carried.ShowCarried();
            Carried = carried;
        }
    }

    /// <summary>요청 하나: 모으기 → 확인 → 보내기 → 받기(흘러오는 글을 띠에) → 틀 읽기.</summary>
    public Task RunAsync(CancellationToken ct) => RunAsync(tier: null, ct);

    /// <summary>등급을 정해 요청한다(null = 기능의 등급).</summary>
    public async Task RunAsync(AiTier? tier, CancellationToken ct)
    {
        if (IsRunning)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        _startedUtc = UtcNow();
        AnswerText = string.Empty;
        Answer = null;
        LastResult = null;
        ShowsRetryOnMain = false;
        IsAccepted = false;
        State = AiStripState.Running;
        RefreshProgress();
        try
        {
            AiStripPreparation preparation;
            try
            {
                preparation = await _prepare(tier, cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceError("AI 띠 준비 중 예외({0}): {1}", Title, Chat.AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
                Finish(AiStripState.Failed, PrepareFailedText);
                return;
            }

            if (preparation.Request is not { } request)
            {
                Finish(AiStripState.Refused, preparation.RefusalText ?? CancelledText);
                return;
            }

            if (Confirm is { } confirm && !await confirm(request, cts.Token).ConfigureAwait(true))
            {
                Finish(AiStripState.Refused, CancelledText);
                return;
            }

            LastRequest = request;
            ModelLabel = request.Run.Route.ModelLabel;
            var result = await request.Run.AskAsync(request.Prompt, resumeId: null, request.Bundle, OnEvent, cts.Token).ConfigureAwait(true);
            LastResult = result;
            AnswerText = result.Text;
            ModelLabel = result.ModelLabel.Length > 0 ? result.ModelLabel : ModelLabel;
            if (!result.Succeeded)
            {
                ShowsRetryOnMain = _canRetryOnMain && result.RetryOnMain;
                Finish(AiStripState.Failed, "오류: " + result.Error);
                return;
            }

            Answer = request.Template?.Parse(result.Text);
            Finish(AiStripState.Done, result.Escalated ? EscalatedText : string.Empty);
        }
        catch (OperationCanceledException)
        {
            Finish(AiStripState.Stopped, "멈춤");
        }
        finally
        {
            _cts = null;
        }
    }

    private void OnEvent(AiEvent e)
    {
        switch (e)
        {
            case AiTextDelta delta:
                AnswerText += delta.Text;
                break;
            case AiModelChosen chosen when !string.IsNullOrWhiteSpace(chosen.Label):
                ModelLabel = chosen.Label; // 답한 모델을 받는 동안에도 보인다
                break;
        }
    }

    private void Finish(AiStripState state, string status)
    {
        StatusText = status;
        State = state;
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(ShowsRawText));
        OnPropertyChanged(nameof(ShowsAccept));
        ContinueInChatCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
    }

    private static async Task DefaultCopyAsync(string text)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
