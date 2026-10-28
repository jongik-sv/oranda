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

/// <summary>접힌 도구 카드: 도구 이름, 응답 크기, 게이트 판단(허용·거절·실행 중 등).</summary>
public sealed record ChatToolEntry(string Name, string ResponseSize, string Verdict, bool Done = true, string? CallId = null) : ChatEntry
{
    /// <summary>카드 앞 상태 표식(시안 9): 끝났으면 ✓, 진행 중이면 …</summary>
    public string Mark => Done ? "✓" : "…";
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
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    private int _toolCount;
    private int _runCount;

    /// <summary>셸이 만든 대화 창 모델(튜닝 명령이 같은 창에 쓴다). 아직 만들어지지 않았으면 null.</summary>
    public static AiChatViewModel? Current { get; private set; }

    public AiChatViewModel()
    {
        Current = this;
    }

    public ObservableCollection<ChatEntry> Entries { get; } = [];

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
        IsRunning = false;
    }

    /// <summary>한 번의 실행을 시작한다. <paramref name="userText"/> 를 사용자 줄로 보인다.</summary>
    public void BeginTurn(string userText)
    {
        Entries.Add(new ChatUserEntry(userText));
        _turnStart = DateTime.UtcNow;
        _runCount++;
        IsRunning = true;
        UpdateStatus();
    }

    public void AddToolCall(string name, string responseSize, string verdict, bool done = true, string? callId = null)
    {
        Entries.Add(new ChatToolEntry(name, responseSize, verdict, done, callId));
        _toolCount++;
        UpdateStatus();
    }

    public void AddAssistantText(string text) => Entries.Add(new ChatAssistantEntry(text));

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
                AddToolCall(call.ToolName, call.ArgumentsSummary, "…", done: false, callId: call.CallId);
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
                Entries[i] = new ChatToolEntry(card.Name, SizeText(result.Size), VerdictText(result.Judgement), true, card.CallId);
                return;
            }
        }

        AddToolCall(result.ToolName, SizeText(result.Size), VerdictText(result.Judgement), callId: result.CallId);
    }

    private static string SizeText(int size) => string.Create(CultureInfo.CurrentCulture, $"{size:N0} 자");

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
        UpdateStatus();
    }

    private bool CanSend() => !IsRunning && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Draft.Trim();
        Draft = string.Empty;
        FollowUpRequested?.Invoke(this, text);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateStatus()
    {
        var seconds = (int)Math.Max(0, (DateTime.UtcNow - _turnStart).TotalSeconds);
        StatusText = _runCount == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"실행 {_runCount}회 · {seconds} 초 · 도구 {_toolCount}개");
    }
}
