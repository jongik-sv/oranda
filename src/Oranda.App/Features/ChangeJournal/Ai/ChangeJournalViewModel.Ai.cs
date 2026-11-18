using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.ChangeJournal.Ai;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Journal;
using KitEntryState = Oranda.App.Features.Ai.Kit.AiEntryState;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>
/// 변경 일지 「이 기간 요약」(설계 §4.2, 바로 처리 — 일지 위 요약 띠). 지금 목록에 보이는 줄 가운데 <b>지금 접속</b>의 변경만 보낸다
/// (일지는 여러 접속을 함께 보이므로, 허용 수준·가림은 한 접속 기준이어야 한다). 기간 = 그 줄들의 첫 시각 ~ 끝 시각.
/// </summary>
public sealed partial class ChangeJournalViewModel
{
    public const string AiSummaryLabel = "이 기간 요약";

    /// <summary>지금 접속을 모를 때.</summary>
    public const string AiNoConnectionText = "지금 접속이 없어 요약할 수 없습니다. 접속을 연 뒤 다시 시도하세요.";

    /// <summary>목록에 지금 접속의 변경이 없을 때.</summary>
    public const string AiNoRowsText = "목록에 지금 접속의 변경이 없습니다.";

    private AiInPlaceHost? _ai;
    private AiInlineStripViewModel? _aiStrip;
    private AiEntryButton? _aiEntry;

    /// <summary>요약 띠(앱 연결 전에는 null).</summary>
    public AiInlineStripViewModel? AiStrip => _aiStrip;

    /// <summary>「이 기간 요약」 단추.</summary>
    public AiEntryButton? AiEntry => _aiEntry;

    /// <summary>띠 아래 안내(「값 없이 요약했습니다」).</summary>
    [ObservableProperty]
    private string _aiNote = string.Empty;

    /// <summary>지금 접속. 셸 = 보이는 세션의 접속.</summary>
    internal Func<ConnectionProfile?> CurrentProfile { get; set; } = () => null;

    /// <summary>AI 연결을 잇는다(셸 생성자와 시험).</summary>
    internal void UseAi(AiInPlaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ai = host;
        _aiStrip = host.CreateStrip(JournalSummaryPrompt.Title, "journal_summary", JournalSummaryPrompt.Tier, AiSource, run =>
            AiInput() is { } input ? JournalSummaryPrompt.Prepare(input, run, host.Masker) : AiStripPreparation.Refuse(AiNoRowsText));
        _aiStrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiInlineStripViewModel.State))
            {
                AiNote = _aiStrip is { State: AiStripState.Done, LastRequest: { } request } strip
                    ? string.Join(" ", JournalSummaryPrompt.Read(strip.AnswerText, request).Notes)
                    : string.Empty;
            }
        };
        _aiEntry = new AiEntryButton(AiSummaryLabel, EvaluateAiEntry, () => _aiStrip.RunAsync(CancellationToken.None));
        Reloaded += (_, _) => _aiEntry.Refresh();
        OnPropertyChanged(nameof(AiStrip));
        OnPropertyChanged(nameof(AiEntry));
    }

    /// <summary>지금 목록에서 지금 접속의 변경을 모은다. 없으면 null.</summary>
    internal JournalSummaryInput? AiInput()
    {
        if (CurrentProfile() is not { } profile)
        {
            return null;
        }

        var entries = new List<JournalSummaryEntry>();
        foreach (var row in _merged)
        {
            switch (row)
            {
                case JournalItem item when item.Entry.ProfileId == profile.Id:
                    entries.Add(new JournalSummaryEntry(item.At, item.Entry.Cause == JournalCause.Ddl ? "DDL" : "COMPILE", item.ObjectText, null)
                    {
                        StatementText = item.Entry.StatementSummary,
                        Outcome = item.IsFailure ? "보관 실패" : null,
                    });
                    break;
                case RollbackGroupItem group when group.Group.ProfileId == profile.Id:
                    entries.AddRange(group.Group.Statements.Select(s => new JournalSummaryEntry(
                        s.ExecutedAt, s.Verb.ToString().ToUpperInvariant(), s.Table is { } t ? RollbackGroupItem.Name(t) : null, s.RowsAffected)
                    {
                        StatementText = s.Summary,
                        Outcome = group.CauseText,
                    }));
                    break;
            }
        }

        return entries.Count == 0 ? null : new JournalSummaryInput(entries.Min(e => e.At), entries.Max(e => e.At), entries);
    }

    private KitEntryState EvaluateAiEntry()
    {
        if (_ai is null)
        {
            return new KitEntryState(false, null);
        }

        var profile = CurrentProfile();
        if (_ai.Configured && profile is null)
        {
            return new KitEntryState(false, AiNoConnectionText);
        }

        return _ai.Entry(profile, AiAccessLevel.Metadata, AiInput() is null ? AiNoRowsText : null,
            "목록에 보이는 지금 접속의 변경을 AI 가 한 단락으로 요약합니다");
    }

    private AiInPlaceSource AiSource()
    {
        var profile = CurrentProfile();
        var input = AiInput();
        var tokens = input is null ? 0 : AiTierRouting.EstimateTokens(string.Join("\n", input.Entries.Select(e => e.ObjectName + " " + e.StatementText)));
        return new AiInPlaceSource(profile, tokens, profile is null ? AiNoConnectionText : input is null ? AiNoRowsText : null);
    }
}
