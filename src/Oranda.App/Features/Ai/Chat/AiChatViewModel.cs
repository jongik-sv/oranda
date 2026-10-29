using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>대화 창에 쌓이는 한 줄의 공통 뿌리(사용자 글·도구 카드·AI 글).</summary>
public abstract record ChatEntry;

/// <summary>사용자가 보낸 글(처음 요청 문구나 이어 말하기).</summary>
public sealed record ChatUserEntry(string Text) : ChatEntry;

/// <summary>AI 가 돌려준 글.</summary>
public sealed record ChatAssistantEntry(string Text) : ChatEntry;

/// <summary>
/// 도구 카드: 도구 이름, 응답 크기, 게이트 판단(허용·거절·실행 중 등). 한 줄 제목·핵심 인자·거부/실패 사유 문장은 <see cref="ToolCardText"/> 표에서 만든다.
/// <see cref="Arguments"/> 는 드라이버의 인자 요약(LLM 이 보낸 글 그대로라 리터럴은 이미 <c>:vN</c> 으로 가려져 있다), <see cref="Note"/> 는 게이트 사유 코드.
/// </summary>
public sealed record ChatToolEntry(string Name, string ResponseSize, string Verdict, bool Done = true, string? CallId = null) : ChatEntry
{
    public string Arguments { get; init; } = string.Empty;

    /// <summary>이 카드가 속한 묶음(한 차례에 이어 부른 도구들). 묶음이 접히면 카드는 감춰진다.</summary>
    public ChatToolGroup? Group { get; init; }

    /// <summary>걸린 초(묶음 합계용, 잴 수 없었으면 0).</summary>
    internal double Seconds { get; init; }

    public string? Note { get; init; }

    /// <summary>부름부터 결과까지 걸린 시간(잴 수 없었거나 아주 짧으면 빈 글).</summary>
    public string Elapsed { get; init; } = string.Empty;

    internal DateTime StartedUtc { get; init; }

    /// <summary>카드 앞 상태 표식(시안 9): 끝났으면 ✓, 진행 중이면 …</summary>
    public string Mark => Done ? "✓" : "…";

    /// <summary>사람 말 한 줄(예 「인덱스 정보 조회 · APS.TB_A」). 모르는 도구는 이름 그대로.</summary>
    public string Headline
    {
        get
        {
            var line = ToolCardText.Describe(Name, Arguments);
            return line.Detail is { } detail ? line.Title + " · " + detail : line.Title;
        }
    }

    /// <summary>거부·실패 사유 문장(없으면 null).</summary>
    public string? Reason => ToolCardText.ReasonText(Note);

    public bool HasReason => Reason is not null;

    /// <summary>펼침 칸: 도구 이름, 인자(가린 글 그대로 한 줄에 하나), 사유 코드 원문.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { "tool: " + Name };
            if (!string.IsNullOrWhiteSpace(Arguments))
            {
                parts.Add(ToolCardText.Expand(Arguments));
            }

            if (!string.IsNullOrWhiteSpace(Note))
            {
                parts.Add("reason: " + Note);
            }

            return string.Join(Environment.NewLine, parts);
        }
    }
}

/// <summary>
/// 도구 카드 묶음의 머리 줄(st-3): 「도구 N개 실행 · 합계 초 · 모두 허용」. 펼치면 아래에 카드 목록이 보인다.
/// 실행 중에는 펼친 채로 두고 차례가 끝나면 접는다. 카드는 <see cref="AiChatViewModel.Entries"/> 에 그대로 있고 <see cref="ChatToolEntry.Group"/> 으로 묶인다.
/// </summary>
public sealed record ChatToolGroupEntry(ChatToolGroup Group) : ChatEntry;

/// <summary>도구 카드 묶음의 상태(펼침·요약 글). 카드가 늘거나 끝날 때 대화 창 모델이 <see cref="Update"/> 로 다시 센다.</summary>
public sealed partial class ChatToolGroup : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded = true;

    /// <summary>머리 줄 오른쪽 글(펼친 채면 「접기」).</summary>
    public string ToggleText => IsExpanded ? "접기" : "펼치기";

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>머리 줄 앞 표식: 진행 중 …, 문제 없이 끝남 ✓, 거부·실패가 있으면 !.</summary>
    [ObservableProperty]
    private string _mark = "…";

    [ObservableProperty]
    private bool _hasProblem;

    internal void Update(IReadOnlyList<ChatToolEntry> cards)
    {
        var running = cards.Any(c => !c.Done);
        var seconds = cards.Sum(c => c.Seconds);
        var parts = new List<string> { string.Create(CultureInfo.CurrentCulture, $"도구 {cards.Count}개 {(running ? "실행 중" : "실행")}") };
        if (seconds >= 0.1)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0} 초"));
        }

        var done = cards.Where(c => c.Done).ToList();
        var counts = done.GroupBy(c => c.Verdict).Select(g => (Verdict: g.Key, Count: g.Count())).ToList();
        if (counts is [var only] && done.Count == cards.Count)
        {
            parts.Add("모두 " + only.Verdict);
        }
        else
        {
            parts.AddRange(counts.Select(c => string.Create(CultureInfo.CurrentCulture, $"{c.Verdict} {c.Count}")));
        }

        Summary = string.Join(" · ", parts);
        HasProblem = done.Any(c => c.Verdict is "거부" or "실패");
        Mark = running ? "…" : HasProblem ? "!" : "✓";
    }
}

/// <summary>
/// 재작성 제안 카드(c6): 비교 창을 거절·닫기 한 뒤에도 대화 창에 남고 「다시 보기」 로 같은 비교 창을 다시 연다. 받아들인 제안은 「적용함」.
/// <see cref="Label"/>·<see cref="Summary"/> 는 AI 가 보낸 글 그대로다(가린 대화면 자리표시 그대로 — 되돌린 SQL 은 <see cref="Reopen"/> 안에만 있다).
/// </summary>
public sealed record ChatProposalEntry(string Label, string Summary, bool Applied) : ChatEntry
{
    /// <summary>벤치마크 경과 변화율(예 「−37%」). 벤치마크가 없으면 null 이고 카드에 안 보인다.</summary>
    public string? Change { get; init; }

    public bool HasChange => !string.IsNullOrEmpty(Change);

    /// <summary>비교 창을 다시 연다. 받아들이면 true, 거절하면 false, 열지 못했으면(탭이 닫힘·문장이 바뀜) null.</summary>
    internal Func<Task<bool?>>? Reopen { get; init; }

    public string Headline => "재작성 제안 · " + Label;

    public bool CanReview => !Applied;
}

/// <summary>
/// AI 대화 도구 창 최소판(시안 8): 도구 카드가 진행하는 모습, 멈춤, 이어 말하기 입력. 대화 기록은 저장하지 않는다(창을 닫으면 사라진다).
/// 호출 쪽(튜닝 실행 흐름)이 <see cref="BeginTurn"/>·<see cref="AddToolCall"/>·<see cref="AddAssistantText"/>·<see cref="EndTurn"/> 을 UI 스레드에서 부른다.
/// 셸이 도구 창으로 만들 때는 매개변수 없는 생성자를 쓰고, 그 인스턴스가 <see cref="Current"/> 가 된다.
/// </summary>
[OrandaToolPanel("oranda.panel.aiChat", ToolPanelKind.Tool, DockLocation.Right, Order = 50)]
public sealed partial class AiChatViewModel : ToolPanelViewModelBase
{
    private DateTime _turnStart;

    [ObservableProperty]
    private string _title = "대화";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(ReviewProposalCommand))]
    [NotifyPropertyChangedFor(nameof(CanType))]
    private bool _isRunning;

    /// <summary>제안 비교 창을 다시 연 채다(그동안 다른 제안을 겹쳐 열지 않는다).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewProposalCommand))]
    private bool _isReviewing;

    /// <summary>
    /// 이어 갈 튜닝 대화가 있다(<see cref="StartConversation"/> 이 한 번이라도 불렸다). 입력은 이어 말하기만 하므로, 없으면 보낸 글이 아무 데도 가지 않는다:
    /// 보내기를 끄고 화면에 「AI 튜닝」 으로 시작하라는 안내를 보인다.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(CanType))]
    private bool _canFollowUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    private int _toolCount;
    private int _runCount;

    /// <summary>지금 카드를 이어 붙일 도구 묶음(차례가 새로 시작되면 끊긴다).</summary>
    private ChatToolGroup? _openGroup;

    /// <summary>셸이 만든 대화 창 모델(튜닝 명령이 같은 창에 쓴다). 아직 만들어지지 않았으면 null.</summary>
    public static AiChatViewModel? Current { get; private set; }

    public AiChatViewModel()
    {
        Current = this;
    }

    public ObservableCollection<ChatEntry> Entries { get; } = [];

    /// <summary>입력 칸에 쓸 수 있다: 이어 갈 대화가 있고 실행 중이 아니다.</summary>
    public bool CanType => CanFollowUp && !IsRunning;

    /// <summary>이어 말하기를 보냈다(인수 = 보낸 글). 호출 쪽이 같은 세션으로 다시 실행한다.</summary>
    public event EventHandler<string>? FollowUpRequested;

    /// <summary>멈춤을 눌렀다.</summary>
    public event EventHandler? StopRequested;

    /// <summary>새 대화를 연다(앞의 줄은 지운다).</summary>
    public void StartConversation(string title)
    {
        Entries.Clear();
        ResumeId = null; // 앞 대화의 claude 세션을 새 대화가 이어 받지 않게
        Title = title;
        Draft = string.Empty;
        StatusText = string.Empty;
        _toolCount = 0;
        _runCount = 0;
        _openGroup = null;
        IsRunning = false;
        CanFollowUp = true;
    }

    /// <summary>한 번의 실행을 시작한다. <paramref name="userText"/> 를 사용자 줄로 보인다.</summary>
    public void BeginTurn(string userText)
    {
        Entries.Add(new ChatUserEntry(userText));
        _openGroup = null;
        _turnStart = DateTime.UtcNow;
        _runCount++;
        IsRunning = true;
        UpdateStatus();
    }

    public void AddToolCall(string name, string responseSize, string verdict, bool done = true, string? callId = null, string arguments = "")
        => AddToolCard(new ChatToolEntry(name, responseSize, verdict, done, callId) { Arguments = arguments });

    /// <summary>카드를 넣는다. 바로 앞이 지금 묶음의 머리 줄·카드가 아니면(차례 시작, 사이에 AI 글) 새 묶음 머리 줄부터 넣는다.</summary>
    private void AddToolCard(ChatToolEntry card)
    {
        var continues = _openGroup is { } open && Entries.Count > 0
            && (Entries[^1] is ChatToolEntry last && last.Group == open || Entries[^1] is ChatToolGroupEntry head && head.Group == open);
        if (!continues)
        {
            _openGroup = new ChatToolGroup { IsExpanded = IsRunning };
            Entries.Add(new ChatToolGroupEntry(_openGroup));
        }

        Entries.Add(card with { Group = _openGroup });
        _toolCount++;
        UpdateGroup(_openGroup!);
        UpdateStatus();
    }

    private void UpdateGroup(ChatToolGroup group) => group.Update(Entries.OfType<ChatToolEntry>().Where(c => c.Group == group).ToList());

    public void AddAssistantText(string text) => Entries.Add(new ChatAssistantEntry(text));

    /// <summary>제안 카드를 남긴다. <paramref name="reopen"/> 은 같은 제안의 비교 창을 다시 여는 호출 쪽 동작, <paramref name="change"/> 는 벤치마크 변화율(없으면 null).</summary>
    public void AddProposal(string label, string summary, bool applied, Func<Task<bool?>> reopen, string? change = null)
        => Entries.Add(new ChatProposalEntry(label, summary, applied) { Reopen = reopen, Change = change });

    /// <summary>앞 대화를 잇는 id(<see cref="AiStarted"/>·<see cref="AiFinal"/> 이 알려 준다). 이어 말하기를 보낼 때 호출 쪽이 읽어 요청에 싣는다.</summary>
    public string? ResumeId { get; private set; }

    /// <summary>
    /// 드라이버 사건 하나를 화면에 옮긴다(UI 스레드). 도구 부름은 진행 중 카드로 두었다가 같은 호출 id 의 결과가 오면 크기·게이트 판단으로 바꾼다.
    /// 글 조각은 마지막 AI 글 줄에 이어 붙이고, 끝·오류가 오면 실행을 끝낸다.
    /// </summary>
    public void Apply(AiEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case AiStarted started:
                ResumeId = started.ResumeId ?? ResumeId;
                break;
            case AiTextDelta delta:
                if (Entries.Count > 0 && Entries[^1] is ChatAssistantEntry last)
                {
                    Entries[^1] = last with { Text = last.Text + delta.Text };
                }
                else
                {
                    AddAssistantText(delta.Text);
                }

                break;
            case AiToolCall call:
                AddToolCard(new ChatToolEntry(call.ToolName, string.Empty, "…", Done: false, call.CallId) { Arguments = call.ArgumentsSummary, StartedUtc = DateTime.UtcNow });
                break;
            case AiToolResult result:
                CompleteToolCall(result);
                break;
            case AiNeedsChoice choice:
                AddAssistantText($"{choice.Question}\n" + string.Join('\n', choice.Options.Select((o, i) => string.Create(CultureInfo.InvariantCulture, $"{i + 1}) {o}"))));
                EndTurn();
                break;
            case AiFinal final:
                ResumeId = final.ResumeId ?? ResumeId;
                if (!string.IsNullOrEmpty(final.Text) && !(Entries.Count > 0 && Entries[^1] is ChatAssistantEntry))
                {
                    AddAssistantText(final.Text);
                }

                EndTurn();
                break;
            case AiError error:
                AddAssistantText("오류: " + error.Message);
                EndTurn();
                break;
        }
    }

    /// <summary>드라이버가 내는 사건 흐름을 끝까지 화면에 옮긴다. 흐름이 끝나거나 취소·예외로 멈추면 실행 중 표시를 내린다.</summary>
    public async Task RunAsync(IAsyncEnumerable<AiEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        try
        {
            await foreach (var e in events.WithCancellation(ct).ConfigureAwait(true))
            {
                Apply(e);
            }
        }
        catch (OperationCanceledException)
        {
            // 멈춤: 아래 finally 가 실행 중 표시를 내린다.
        }
        finally
        {
            EndTurn();
        }
    }

    private void CompleteToolCall(AiToolResult result)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] is ChatToolEntry { Done: false } card && card.CallId == result.CallId)
            {
                var seconds = SecondsSince(card.StartedUtc);
                Entries[i] = card with
                {
                    ResponseSize = SizeText(result.Size), Verdict = VerdictText(result.Judgement), Done = true, Note = result.Note,
                    Elapsed = ElapsedText(seconds), Seconds = seconds,
                };
                if (card.Group is { } group)
                {
                    UpdateGroup(group);
                }

                return;
            }
        }

        AddToolCard(new ChatToolEntry(result.ToolName, SizeText(result.Size), VerdictText(result.Judgement), true, result.CallId) { Note = result.Note });
    }

    private static string SizeText(int size) => string.Create(CultureInfo.CurrentCulture, $"{size:N0} 자");

    private static double SecondsSince(DateTime startedUtc) => startedUtc == default ? 0 : Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);

    private static string ElapsedText(double seconds) =>
        seconds < 0.1 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0} 초");

    private static string VerdictText(AiToolJudgement judgement) => judgement switch
    {
        AiToolJudgement.Allowed => "허용",
        AiToolJudgement.Masked => "가림",
        AiToolJudgement.Denied => "거부",
        AiToolJudgement.Failed => "실패",
        _ => judgement.ToString(),
    };

    /// <summary>실행이 끝났거나 멈췄다.</summary>
    public void EndTurn()
    {
        IsRunning = false;
        foreach (var group in Entries.OfType<ChatToolGroupEntry>())
        {
            group.Group.IsExpanded = false; // 끝난 차례의 도구 카드는 한 줄 요약으로 접는다
        }

        UpdateStatus();
    }

    private bool CanSend() => CanType && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Draft.Trim();
        Draft = string.Empty;
        FollowUpRequested?.Invoke(this, text);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);

    private bool CanReviewProposal(ChatProposalEntry? entry) => entry is { Applied: false, Reopen: not null } && !IsRunning && !IsReviewing;

    /// <summary>「다시 보기」: 제안 비교 창을 다시 연다. 받아들이면 카드를 「적용함」 으로 바꾼다. 실행 중에는 열지 않는다.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewProposal))]
    private async Task ReviewProposalAsync(ChatProposalEntry? entry)
    {
        if (entry?.Reopen is not { } reopen)
        {
            return;
        }

        IsReviewing = true;
        try
        {
            if (await reopen().ConfigureAwait(true) == true && Entries.IndexOf(entry) is >= 0 and var i)
            {
                Entries[i] = entry with { Applied = true };
            }
        }
        finally
        {
            IsReviewing = false;
        }
    }

    private void UpdateStatus()
    {
        var seconds = (int)Math.Max(0, (DateTime.UtcNow - _turnStart).TotalSeconds);
        StatusText = _runCount == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"실행 {_runCount}회 · {seconds} 초 · 도구 {_toolCount}개");
    }
}
