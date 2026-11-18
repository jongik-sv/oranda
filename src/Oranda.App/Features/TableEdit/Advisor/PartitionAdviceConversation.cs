using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>대화가 부르는 바깥 동작(객체 상세가 넣는다).</summary>
public sealed record AdviceConversationHost
{
    /// <summary>「편집기에 채우기」. 채웠으면 true.</summary>
    public Func<AdvisorCandidate, bool>? Fill { get; init; }

    /// <summary>보고서 파일 고르기(제안 이름 → 경로, 취소 = null). 없으면 「보고서로 저장」을 끈다.</summary>
    public Func<string, CancellationToken, Task<string?>>? PickReportFile { get; init; }
}

/// <summary>
/// AI 파티션 추천 대화(설계 PA-22): 확인 창이 모은 근거로 AI 대화 창에 대화를 열고(<see cref="AiChatViewModel.StartConversation(string, IAiChatConversation)"/>),
/// 응답을 대화 창에 흘리고, 구조 결과가 오면 응답 글(JSON)을 걷어 내고 요약 · 후보 카드(AI 이유 + 앱 확인) · 마무리 카드(원문·not_chosen·보고서·후보 3개 받기)를 넣는다.
/// 이어 묻기는 같은 대화(ResumeId)로 같은 근거·같은 금고를 써서 보내고, 새 구조 결과마다 카드와 앱 확인을 다시 만든다. 차례마다 새 실행 범위를 싣고 끝나면 폐기한다
/// (<see cref="PartitionAdvisorRun.AskAsync"/>). 대화 창이 새 대화로 바뀌면 이 대화는 더 쓰지 않는다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "취소 토큰은 한 차례 동안만 쥐고 끝나면 using 으로 정리한다.")]
public sealed class PartitionAdviceConversation : IAiChatConversation
{
    private readonly AiChatViewModel _chat;
    private readonly PartitionAdvisorRun _run;
    private readonly PartitionEvidence _evidence;
    private readonly AdvicePromptOptions _options;
    private readonly AdviceConversationHost _host;
    private readonly List<AdviceCandidateCard> _cards = [];
    private readonly List<AdviceResultCard> _results = [];
    private CancellationTokenSource? _cts;
    private int _generation = -1;

    public PartitionAdviceConversation(AiChatViewModel chat, PartitionAdvisorRun run, PartitionEvidence evidence, AdvicePromptOptions options, AdviceConversationHost host)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        _chat = chat;
        _run = run;
        _evidence = evidence;
        _options = run.Normalize(options);
        _host = host;
    }

    /// <summary>대화 창 제목.</summary>
    public string Title => TableEditText.T("AI 파티션 추천 — ", "AI partition advice — ") + _evidence.Table;

    public bool IsRunning => _cts is not null;

    /// <summary>대화 창의 지금 대화가 이 대화다(새 대화로 바뀌지 않았다).</summary>
    public bool IsCurrent => ReferenceEquals(_chat.Owner, this) && _chat.ConversationGeneration == _generation;

    /// <summary>다시 물을 수 있다(「후보 3개 받기」).</summary>
    public bool CanAsk => IsCurrent && !IsRunning && !_chat.IsRunning;

    public bool CanSaveReport => _host.PickReportFile is not null;

    /// <summary>지금까지 대화에 넣은 후보 카드(모든 차례).</summary>
    public IReadOnlyList<AdviceCandidateCard> Cards => _cards;

    /// <summary>차례마다의 마무리 카드.</summary>
    public IReadOnlyList<AdviceResultCard> Results => _results;

    /// <summary>마지막으로 받은 결과(없으면 null).</summary>
    public AdvisorOutcome? LastOutcome { get; private set; }

    /// <summary>대화를 열고 첫 요청을 보낸다.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _chat.StartConversation(Title, this);
        _generation = _chat.ConversationGeneration;
        await TurnAsync(FirstLine(), question: null, _options, ct).ConfigureAwait(true);
    }

    public async Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        if (!IsCurrent)
        {
            _chat.RestoreFollowUp(followUp);
            return;
        }

        if (IsRunning || _chat.IsRunning)
        {
            _chat.AddAssistantText(TableEditText.T("앞 요청이 끝난 뒤 다시 보내세요.", "Send again after the current request ends."));
            _chat.RestoreFollowUp(followUp);
            return;
        }

        if (followUp.Images.Count > 0)
        {
            _chat.AddAssistantText(TableEditText.T("AI 파티션 추천 대화에는 이미지를 보낼 수 없습니다. 글만 보내세요.", "Images cannot be sent in the AI partition advice chat. Send text only."));
            _chat.RestoreFollowUp(followUp);
            return;
        }

        if (string.IsNullOrWhiteSpace(followUp.Text))
        {
            _chat.RestoreFollowUp(followUp);
            return;
        }

        await TurnAsync(followUp.Text, followUp.Text, _options, ct).ConfigureAwait(true);
    }

    /// <summary>⑤ 「그래도 후보 3개 받기」: 같은 대화에서 후보를 꼭 내라고 다시 묻는다.</summary>
    public Task RequestCandidatesAsync() => !CanAsk
        ? Task.CompletedTask
        : TurnAsync(
            TableEditText.T("후보 3개 받기", "Get 3 options"),
            TableEditText.T("파티션을 한다면 어떤 후보가 있는지 1~3개 내 달라.", "If the table were partitioned, give 1 to 3 options."),
            _options with { RequireCandidates = true },
            CancellationToken.None);

    public void Stop() => _cts?.Cancel();

    public void CancelRun() => Stop();

    /// <summary>「보고서로 저장…」(HTML): 그 차례의 결과와 근거. 저장 결과는 대화 창에 한 줄로 남긴다.</summary>
    public async Task SaveReportAsync(AdviceResultCard result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_host.PickReportFile is not { } pick)
        {
            return;
        }

        var outcome = result.Outcome;
        var title = Title + TableEditText.T(" · 받은 곳 ", " · from ") + _run.Route.Target.Label;
        var (path, error) = await PartitionAdviceReport.SaveAsync(pick, _evidence.Table, () => PartitionAdviceReport.Render(
            title, new PartitionEvidenceViewModel(outcome.Evidence, outcome.Vault, outcome.Options.SendValues), outcome, result.Cards, DateTime.Now), CancellationToken.None).ConfigureAwait(true);
        if (error is not null)
        {
            _chat.AddAssistantText(TableEditText.T("보고서를 저장하지 못했습니다: ", "Could not save the report: ") + error);
        }
        else if (path is not null)
        {
            _chat.AddAssistantText(TableEditText.T("보고서를 저장했습니다: ", "Saved the report: ") + path);
        }
    }

    private string FirstLine()
    {
        var goal = _options.Goal switch
        {
            AdviceGoal.Query => TableEditText.T("조회 성능", "query performance"),
            AdviceGoal.Purge => TableEditText.T("오래된 데이터 정리", "purging old data"),
            _ => TableEditText.T("조회 성능 + 오래된 데이터 정리", "query performance + purging old data"),
        };
        var values = _options.SendValues ? TableEditText.T("실제 값 보냄", "real values sent") : TableEditText.T("값 가림", "values hidden");
        return TableEditText.T("AI 파티션 추천", "AI partition advice") + $" · {_evidence.Owner}.{_evidence.Table} · " + goal + " · " + values;
    }

    private async Task TurnAsync(string userLine, string? question, AdvicePromptOptions options, CancellationToken ct)
    {
        _chat.BeginTurn(userLine);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        Refresh();
        AdvisorOutcome? outcome = null;
        try
        {
            var turn = new AdviceTurn(question, question is null ? null : _chat.ResumeId, e =>
            {
                if (IsCurrent)
                {
                    _chat.Apply(e); // 글 조각·이어받기 id·끝·오류를 대화 창에(오류 글도 대화 창이 남긴다)
                }
            });
            outcome = await _run.AskAsync(_evidence, options, cts.Token, turn).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 멈춤: 대화 창이 「멈춤」 결과 줄을 보인다.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsCurrent)
            {
                _chat.AddAssistantText(TableEditText.T("오류: ", "Error: ") + AiErrorText.Redact(ex.Message));
            }
        }
        finally
        {
            _cts = null;
            if (IsCurrent)
            {
                _chat.EndTurn();
            }

            Refresh();
        }

        if (outcome is not null && IsCurrent)
        {
            Show(outcome);
        }
    }

    /// <summary>한 차례 결과를 대화 창에: 읽었으면 응답 글(JSON)을 요약으로 바꾸고 후보 카드·마무리 카드를, 못 읽었으면 원문 글을 두고 안내 카드를.</summary>
    private void Show(AdvisorOutcome outcome)
    {
        if (outcome.Advice is null && outcome.Error is not null)
        {
            return; // 대화 창이 오류 글을 이미 남겼다
        }

        LastOutcome = outcome;
        if (outcome.Advice is { } advice)
        {
            RemoveTurnAnswer();
            var head = string.Equals(advice.Verdict, AdviceVerdict.NoPartition, StringComparison.OrdinalIgnoreCase)
                ? "**" + TableEditText.T("파티션 안 함 권장", "No partitioning recommended") + "**"
                : string.Empty;
            var summary = string.Join("\n\n", new[] { head, advice.Summary }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (summary.Length > 0)
            {
                _chat.AddAssistantText(summary);
            }
        }

        var cards = outcome.Candidates.Select(c => new AdviceCandidateCard(c, Fill)).ToList();
        foreach (var card in cards)
        {
            _cards.Add(card);
            _chat.AddCard(card);
        }

        var result = new AdviceResultCard(this, outcome, cards);
        _results.Add(result);
        _chat.AddCard(result);
    }

    /// <summary>이 차례의 AI 글 줄(구조 결과의 JSON 그대로)을 걷어 낸다. 원문은 마무리 카드의 「AI 원문 보기」에 남는다.</summary>
    private void RemoveTurnAnswer()
    {
        var start = -1;
        for (var i = _chat.Entries.Count - 1; i >= 0; i--)
        {
            if (_chat.Entries[i] is ChatUserEntry)
            {
                start = i;
                break;
            }
        }

        for (var i = _chat.Entries.Count - 1; i > start; i--)
        {
            if (_chat.Entries[i] is ChatAssistantEntry)
            {
                _chat.Entries.RemoveAt(i);
            }
        }
    }

    private bool Fill(AdviceCandidateCard card)
    {
        if (_host.Fill is not { } fill || !fill(card.Candidate))
        {
            if (IsCurrent)
            {
                _chat.AddAssistantText(TableEditText.T("편집기에 채우지 못했습니다(편집을 끄거나 파티션을 고칠 수 없는 상태)", "Could not fill the editor (editing is off or partitions cannot be edited)"));
            }

            return false;
        }

        foreach (var other in _cards)
        {
            other.IsFilled = ReferenceEquals(other, card);
        }

        if (IsCurrent)
        {
            var rank = card.Candidate.Candidate.Rank;
            _chat.AddAssistantText(TableEditText.T($"{rank}안을 파티션 편집기에 채웠습니다. DDL 미리보기를 확인하세요.", $"Filled option {rank} into the partition editor. Check the DDL preview."));
        }

        return true;
    }

    private void Refresh()
    {
        foreach (var result in _results)
        {
            result.Refresh();
        }
    }
}
