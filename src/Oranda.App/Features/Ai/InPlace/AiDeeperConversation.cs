using System.Text;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;

namespace Oranda.App.Features.Ai.InPlace;

/// <summary>
/// AI 띠의 「더 깊게」(설계 §1.2 「이어 묻기」): 주 모델로 채팅창에 <b>새 대화</b>를 연다. 띠가 보낸 대화는 넘기지 않고, 화면의 지금 문맥을 주 모델 경로로 다시 모아
/// (받는 곳·가림이 주 모델 기준으로 다시 계산된다) 자세한 풀이를 자유 글로 받는다. 이어 묻기는 같은 주 모델 경로로 간다.
/// </summary>
public sealed class AiDeeperConversation : AiFeatureConversation
{
    /// <summary>첫 요청 끝에 붙이는 지시(답 틀 대신).</summary>
    public const string DeeperRule =
        "Go deeper than a short summary: explain the reasoning, what to check next and how, and the risks of each option. "
        + "Answer in Korean in Markdown. Never write tool or function names.";

    private readonly AiStripRequest _request;
    private readonly List<(string Question, string Answer)> _turns = [];
    private string? _firstAnswer;
    private string? _resumeId;

    /// <param name="chat">채팅창.</param>
    /// <param name="title">대화 제목(「락 풀이 · 더 깊게」).</param>
    /// <param name="request">주 모델 경로로 다시 모은 요청(경고 창을 거친 것).</param>
    public AiDeeperConversation(AiChatViewModel chat, string title, AiStripRequest request)
        : base(chat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
        _request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public override string Title { get; }

    protected override AiKitRoute? Route => _request.Run.Route;

    protected override string? ResumeIdForFollowUp => _resumeId;

    /// <summary>첫 요청 글: 지시 → 깊게 풀라는 규칙 → 꾸러미(답 틀은 싣지 않는다).</summary>
    public string FirstPrompt => _request.Instruction.Trim() + "\n\n" + DeeperRule + "\n\n" + _request.Bundle.ToPrompt();

    protected override async Task RunTurnAsync(AiFeatureTurn turn, CancellationToken ct)
    {
        string prompt;
        if (turn.Question is not { } question)
        {
            prompt = FirstPrompt;
        }
        else if (turn.ResumeId is not null)
        {
            prompt = "# Follow-up\n" + question + "\nAnswer in Korean, in Markdown. Never write tool or function names.";
        }
        else
        {
            var history = new StringBuilder(FirstPrompt);
            history.Append("\n\n<previous_answer>\n").Append(_firstAnswer).Append("\n</previous_answer>\n");
            foreach (var (q, a) in _turns)
            {
                history.Append("<previous_question>\n").Append(q).Append("\n</previous_question>\n<previous_answer>\n").Append(a).Append("\n</previous_answer>\n");
            }

            prompt = history.Append("\n# Follow-up\n").Append(question).Append("\nAnswer in Korean, in Markdown.").ToString();
        }

        var result = await _request.Run.AskAsync(prompt, turn.ResumeId, _request.Bundle, turn.OnEvent, ct).ConfigureAwait(true);
        _resumeId = result.ResumeId ?? _resumeId;
        if (!result.Succeeded)
        {
            return;
        }

        if (turn.Question is { } asked)
        {
            _turns.Add((asked, result.Text));
        }
        else
        {
            _firstAnswer = result.Text;
        }
    }
}
