using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>대화 한 차례의 재료.</summary>
/// <param name="Question">이어 묻기 글(null = 첫 요청).</param>
/// <param name="ResumeId">앞 대화를 잇는 id(없으면 null — 앞 문맥을 다시 실어야 한다).</param>
/// <param name="OnEvent">드라이버 사건을 채팅창에 옮긴다(대화가 바뀌었으면 버린다).</param>
public sealed record AiFeatureTurn(string? Question, string? ResumeId, Action<AiEvent> OnEvent);

/// <summary>
/// 기능 대화 주인의 공통부(설계 §2, 본보기 <c>PartitionAdviceConversation</c>): 채팅창에 새 대화를 열고(<see cref="Open"/>), 차례마다 진행 표시·멈춤·오류 글·끝을 맡는다.
/// 이어 묻기의 공통 거절(대화가 바뀜·진행 중·이미지·빈 글·가리는 대화의 글 확인)도 여기서 한다. 기능은 <see cref="RunTurnAsync"/> 에서 보내고 받은 결과로 카드를 넣는다.
/// 채팅창이 새 대화로 바뀌면(<see cref="IsCurrent"/> 거짓) 이 대화는 더 쓰지 않는다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "취소 토큰은 한 차례 동안만 쥐고 끝나면 using 으로 정리한다.")]
public abstract class AiFeatureConversation : IAiChatConversation
{
    /// <summary>앞 요청이 끝나지 않았을 때.</summary>
    public const string BusyText = "앞 요청이 끝난 뒤 다시 보내세요.";

    /// <summary>이미지를 받지 않는 대화.</summary>
    public const string ImagesRefusedText = "이 대화에는 이미지를 보낼 수 없습니다. 글만 보내세요.";

    /// <summary>가리는 대화인데 글 확인 창을 열 수 없을 때.</summary>
    public const string TextConfirmMissingText = "이 대화는 값을 가리는 중이라 입력한 글을 보내기 전에 확인이 필요한데 확인 창을 열 수 없어 보내지 않았습니다.";

    private CancellationTokenSource? _cts;
    private int _generation = -1;
    private bool _textConfirmed;

    protected AiFeatureConversation(AiChatViewModel chat)
    {
        Chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    protected AiChatViewModel Chat { get; }

    /// <summary>채팅창 제목.</summary>
    public abstract string Title { get; }

    /// <summary>받는 곳(가리는 대화의 글 확인 창에 쓴다). 없으면 확인하지 않는다.</summary>
    protected virtual AiKitRoute? Route => null;

    /// <summary>가리는 대화의 글 확인 창(대화당 한 번). null 이면 가리는 대화의 이어 묻기를 보내지 않는다.</summary>
    public Func<AiImageSendNotice, CancellationToken, Task<bool>>? ConfirmText { get; init; }

    public bool IsRunning => _cts is not null;

    /// <summary>채팅창의 지금 대화가 이 대화다.</summary>
    public bool IsCurrent => ReferenceEquals(Chat.Owner, this) && Chat.ConversationGeneration == _generation;

    /// <summary>새로 물을 수 있다(카드 단추).</summary>
    public bool CanAsk => IsCurrent && !IsRunning && !Chat.IsRunning;

    /// <summary>채팅창에 이 대화를 연다(앞 대화는 놓인다). <see cref="AiChatOpener.OpenAsync"/> 가 부른다.</summary>
    public void Open()
    {
        Chat.StartConversation(Title, this);
        _generation = Chat.ConversationGeneration;
    }

    /// <summary>첫 요청을 보낸다(<see cref="Open"/> 뒤).</summary>
    public Task StartAsync(string userLine, CancellationToken ct = default) => TurnAsync(userLine, question: null, ct);

    public async Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        if (!IsCurrent || string.IsNullOrWhiteSpace(followUp.Text) && followUp.Images.Count == 0)
        {
            Chat.RestoreFollowUp(followUp);
            return;
        }

        if (IsRunning || Chat.IsRunning)
        {
            Chat.AddAssistantText(BusyText);
            Chat.RestoreFollowUp(followUp);
            return;
        }

        if (followUp.Images.Count > 0)
        {
            Chat.AddAssistantText(ImagesRefusedText);
            Chat.RestoreFollowUp(followUp);
            return;
        }

        if (!await ConfirmTextOnceAsync(ct).ConfigureAwait(true))
        {
            Chat.RestoreFollowUp(followUp);
            return;
        }

        await TurnAsync(followUp.Text, followUp.Text, ct).ConfigureAwait(true);
    }

    public void CancelRun() => _cts?.Cancel();

    /// <summary>한 차례: 사용자 줄 → 보냄(<see cref="RunTurnAsync"/>) → 끝. 멈춤은 채팅창이 「멈춤」 결과 줄을 보인다.</summary>
    protected async Task TurnAsync(string userLine, string? question, CancellationToken ct)
    {
        Chat.BeginTurn(userLine);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        OnStateChanged();
        try
        {
            var turn = new AiFeatureTurn(question, question is null ? null : ResumeIdForFollowUp, e =>
            {
                if (IsCurrent)
                {
                    Chat.Apply(e);
                }
            });
            await RunTurnAsync(turn, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 멈춤
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsCurrent)
            {
                Chat.AddAssistantText("오류: " + AiErrorText.Redact(ex.Message));
            }
        }
        finally
        {
            _cts = null;
            if (IsCurrent)
            {
                Chat.EndTurn();
            }

            OnStateChanged();
        }
    }

    /// <summary>이어 묻기에 실을 id. 기본 = 채팅창이 받은 id.</summary>
    protected virtual string? ResumeIdForFollowUp => Chat.ResumeId;

    /// <summary>보내고 받는다. 사건은 <see cref="AiFeatureTurn.OnEvent"/> 로 넘긴다. 결과 카드는 <see cref="IsCurrent"/> 일 때만 넣는다.</summary>
    protected abstract Task RunTurnAsync(AiFeatureTurn turn, CancellationToken ct);

    /// <summary>진행 상태가 바뀌었다(카드 단추 다시 계산).</summary>
    protected virtual void OnStateChanged()
    {
    }

    private async Task<bool> ConfirmTextOnceAsync(CancellationToken ct)
    {
        if (_textConfirmed || Route is not { MaskRequired: true } route)
        {
            return true;
        }

        if (ConfirmText is not { } confirm)
        {
            Chat.AddAssistantText(TextConfirmMissingText);
            return false;
        }

        var context = route.Context;
        var notice = new AiImageSendNotice(
            string.IsNullOrEmpty(context.ConnectionName) ? "(연결 없음)" : context.ConnectionName, context.Environment ?? ConnectionEnvironment.Development,
            route.Target.Label, route.Target.RecipientUrl, Count: 0, TotalBytes: 0, Masking: true, context.AccessLevel);
        _textConfirmed = await confirm(notice, ct).ConfigureAwait(true);
        return _textConfirmed;
    }
}

/// <summary>
/// 「채팅에서 이어 보기」 로 넘어온 대화(설계 §2.1): AI 띠가 보낸 문맥과 받은 답을 그대로 첫 턴으로 보이고(다시 보내지 않는다), 이어 묻기는 같은 받는 곳·같은 모델
/// (같은 <see cref="AiOneShotRun"/>)으로 간다. 이어받기 id 가 없으면 앞 요청과 답을 데이터로 다시 싣는다.
/// </summary>
public sealed class AiCarriedConversation : AiFeatureConversation
{
    private readonly AiOneShotRun _run;
    private readonly string _firstPrompt;
    private readonly string _firstAnswer;
    private readonly List<(string Question, string Answer)> _turns = [];
    private string? _resumeId;

    /// <param name="chat">채팅창.</param>
    /// <param name="title">대화 제목(띠 이름).</param>
    /// <param name="run">띠가 쓴 같은 요청 경로.</param>
    /// <param name="firstPrompt">띠가 보낸 글 그대로.</param>
    /// <param name="answer">띠가 받은 답 그대로.</param>
    /// <param name="resumeId">띠의 요청이 받은 이어받기 id.</param>
    public AiCarriedConversation(AiChatViewModel chat, string title, AiOneShotRun run, string firstPrompt, string answer, string? resumeId)
        : base(chat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(firstPrompt);
        Title = title;
        _run = run;
        _firstPrompt = firstPrompt;
        _firstAnswer = answer ?? string.Empty;
        _resumeId = resumeId;
    }

    public override string Title { get; }

    /// <summary>답한 모델(띠와 같다).</summary>
    public string ModelLabel => _run.Route.ModelLabel;

    protected override AiKitRoute? Route => _run.Route;

    protected override string? ResumeIdForFollowUp => _resumeId;

    /// <summary>첫 턴의 사용자 줄.</summary>
    public string FirstLine => "「" + Title + "」 — 화면에서 모은 정보를 보냈습니다";

    /// <summary>모델 줄.</summary>
    public string ModelLine => "답한 모델: " + ModelLabel + " · 이어 물으면 같은 모델로 갑니다.";

    /// <summary>띠의 문맥과 답을 첫 턴으로 보인다(보내지 않는다). <see cref="AiFeatureConversation.Open"/> 뒤에 부른다.</summary>
    public void ShowCarried()
    {
        if (!IsCurrent)
        {
            return;
        }

        Chat.Entries.Add(new ChatUserEntry(FirstLine));
        Chat.AddAssistantText(_firstAnswer);
        Chat.AddAssistantText(ModelLine);
    }

    /// <summary>이어 묻기 글. 이어받기 id 가 없으면 앞 요청·답을 데이터로 다시 싣는다.</summary>
    internal string FollowUpPrompt(string question, string? resumeId)
    {
        var follow = "# Follow-up\n" + question + "\nAnswer in Korean, briefly, in Markdown. Never write tool or function names.";
        if (resumeId is not null)
        {
            return follow;
        }

        var history = new System.Text.StringBuilder(_firstPrompt);
        history.Append("\n\n<previous_answer>\n").Append(_firstAnswer).Append("\n</previous_answer>\n");
        foreach (var (q, a) in _turns)
        {
            history.Append("<previous_question>\n").Append(q).Append("\n</previous_question>\n<previous_answer>\n").Append(a).Append("\n</previous_answer>\n");
        }

        return history.Append('\n').Append(follow).ToString();
    }

    protected override async Task RunTurnAsync(AiFeatureTurn turn, CancellationToken ct)
    {
        var question = turn.Question ?? throw new InvalidOperationException("이어 보기 대화의 첫 턴은 보내지 않는다.");
        var result = await _run.AskAsync(FollowUpPrompt(question, turn.ResumeId), turn.ResumeId, bundle: null, turn.OnEvent, ct).ConfigureAwait(true);
        _resumeId = result.ResumeId ?? _resumeId;
        if (result.Succeeded)
        {
            _turns.Add((question, result.Text));
        }
    }
}
