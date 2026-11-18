using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Features.SqlMonitor.Ai;

/// <summary>카드 단추가 부르는 바깥 동작(SQL 모니터가 넣는다).</summary>
public sealed record AwrCauseHost
{
    /// <summary>「편집기에 넣기」: 새 편집기 탭에 글을 넣는다(제목, 본문 → 넣었으면 true). 실행하지 않는다.</summary>
    public Func<string, string, bool>? OpenEditor { get; init; }

    /// <summary>「복사」.</summary>
    public Func<string, Task>? CopyText { get; init; }
}

/// <summary>
/// AWR 「계획이 바뀐 원인」 대화(설계 §3.1·§2.1 채팅창, Kit 의 <see cref="AiFeatureConversation"/>): 첫 요청은 화면 꾸러미 + 답 틀을 주 모델로 보내고
/// (요청마다 새 실행 범위 — 대상 sql_id·전문·파싱 스키마를 실어 AWR·통계 도구가 그 범위에서만 돈다), 받은 답을 기능 카드로 바꾼다.
/// 「고정할 계획」 이 이력의 계획이면 카드에 SQL Plan Baseline 스크립트(앱이 만든 글)를 「편집기에 넣기」 로 붙인다. 이어 묻기는 같은 대화·같은 경로다.
/// AI 는 아무것도 실행하지 않는다.
/// </summary>
public sealed class AwrCauseConversation : AiFeatureConversation
{
    private readonly AiOneShotRun _run;
    private readonly AwrCauseFacts _facts;
    private readonly IAiContextBundle _bundle;
    private readonly AwrCauseHost _host;
    private readonly List<(string Question, string Answer)> _turns = [];
    private string? _firstPrompt;
    private string? _resumeId;

    public AwrCauseConversation(AiChatViewModel chat, AiOneShotRun run, AwrCauseFacts facts, IAiContextBundle bundle, AwrCauseHost? host = null)
        : base(chat)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _host = host ?? new AwrCauseHost();
    }

    public override string Title => AwrCausePrompt.Title + " — " + _facts.SqlId;

    /// <summary>마지막으로 넣은 원인 카드(없으면 null).</summary>
    public AiFeatureCard? LastCard { get; private set; }

    protected override AiKitRoute? Route => _run.Route;

    protected override string? ResumeIdForFollowUp => _resumeId;

    /// <summary>첫 턴 사용자 줄: 무엇을 보냈고 무엇을 뺐는지(허용 수준·가림 까닭).</summary>
    public string FirstLine
    {
        get
        {
            var line = "「" + AwrCausePrompt.Title + "」 — 화면에서 모은 정보를 보냈습니다 (sql_id " + _facts.SqlId
                + string.Create(CultureInfo.InvariantCulture, $" · 계획 {_facts.PlanHashes.Count}개)");
            line += "\n보낸 것: " + string.Join(", ", _bundle.Items.Select(i => i.Label)) + (_bundle.Masked ? " (SQL 값 가림)" : string.Empty);
            if (_bundle.Omitted.Count > 0)
            {
                line += "\n보내지 않음: " + string.Join(", ", _bundle.Omitted.Select(o => o.Label + " — " + o.ReasonText));
            }

            return line;
        }
    }

    /// <summary>첫 요청을 보낸다(<see cref="AiFeatureConversation.Open"/> 뒤).</summary>
    public Task StartAsync(CancellationToken ct = default) => StartAsync(FirstLine, ct);

    protected override async Task RunTurnAsync(AiFeatureTurn turn, CancellationToken ct)
    {
        var first = turn.Question is null;
        _firstPrompt ??= AwrCausePrompt.FirstPrompt(_facts, _bundle);
        var prompt = first ? _firstPrompt : AwrCausePrompt.FollowUpPrompt(turn.Question!, _firstPrompt, _turns, turn.ResumeId);
        var start = Chat.Entries.Count;
        var tools = 0;
        var result = await _run.AskAsync(prompt, turn.ResumeId, _bundle, e =>
        {
            if (e is AiToolCall)
            {
                tools++;
            }

            turn.OnEvent(e);
        }, ct).ConfigureAwait(true);
        _resumeId = result.ResumeId ?? _resumeId;
        if (!result.Succeeded)
        {
            return; // 오류 글은 사건(AiError)으로 이미 채팅창에 있다
        }

        _turns.Add((turn.Question ?? string.Empty, result.Text));
        if (!first || !IsCurrent)
        {
            return; // 이어 묻기의 답은 받은 글 그대로 둔다
        }

        // 흘러온 답 글을 걷어 내고 같은 글을 기능 카드로 넣는다(도구 카드는 남긴다).
        for (var i = Chat.Entries.Count - 1; i >= start; i--)
        {
            if (Chat.Entries[i] is ChatAssistantEntry)
            {
                Chat.Entries.RemoveAt(i);
            }
        }

        var answer = AwrCausePrompt.Template.Parse(result.Text);
        var pinned = AwrCausePrompt.PinnedPlan(answer, _facts);
        var label = string.IsNullOrWhiteSpace(result.ModelLabel) ? _run.Route.ModelLabel : result.ModelLabel;
        var card = new AiFeatureCard(AwrCausePrompt.Title, tools > 0 ? string.Create(CultureInfo.InvariantCulture, $"{label} · 도구 {tools}번") : label, result.Text, answer)
        {
            InsertText = pinned is { } hash ? AwrCausePrompt.BaselineScript(_facts, hash) : null,
            Insert = _host.OpenEditor is { } open && pinned is { } h
                ? text => open(string.Create(CultureInfo.InvariantCulture, $"baseline_{_facts.SqlId}_{h}.sql"), text)
                : null,
            CopyText = _host.CopyText,
        };
        LastCard = card;
        Chat.AddCard(card);
    }
}
