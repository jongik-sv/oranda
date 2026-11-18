using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>앱 확인 한 줄(✔/⚠/✖ + 문구).</summary>
public sealed record AdviceCheckLine(AdviceCheckLevel Level, string Mark, string Text)
{
    public bool IsStop => Level == AdviceCheckLevel.Stop;

    public bool IsWarn => Level == AdviceCheckLevel.Warn;
}

/// <summary>
/// AI 대화 창의 후보 카드 하나(시안 ③, PA-22): AI 이유 · 주의 · 앱 확인 ✔⚠✖ · 「편집기에 채우기」. 튜닝 재작성 제안 카드와 같은 자리(대화 줄)에 들어간다.
/// ✖ 가 있으면 채우기를 끈다. 채우기 동작은 대화(<see cref="PartitionAdviceConversation"/>)가 넣는다.
/// </summary>
public sealed partial class AdviceCandidateCard : ObservableObject
{
    private readonly Func<AdviceCandidateCard, bool>? _fill;

    public AdviceCandidateCard(AdvisorCandidate candidate, Func<AdviceCandidateCard, bool>? fill = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        _fill = fill;
        var c = candidate.Candidate;
        Header = TableEditText.T($"{c.Rank}안 ", $"Option {c.Rank} ") + c.Title;
        Shape = Describe(c);
        Checks = [.. candidate.AllChecks.Select(k => new AdviceCheckLine(k.Level, PartitionAdvisorText.Mark(k.Level), PartitionAdvisorText.Check(k)))];
    }

    public AdvisorCandidate Candidate { get; }

    public string Header { get; }

    /// <summary>방식 요약(「RANGE(WORK_YMD) · 간격 none · 하위 LIST(PLANT_CD)」).</summary>
    public string Shape { get; }

    public bool IsTop => Candidate.Candidate.Rank == 1;

    public IReadOnlyList<string> Reasons => Candidate.Candidate.Reasons;

    public IReadOnlyList<string> Cautions => Candidate.Candidate.Cautions;

    public bool HasCautions => Cautions.Count > 0;

    public IReadOnlyList<AdviceCheckLine> Checks { get; }

    /// <summary>✖ 가 없다(「편집기에 채우기」를 켬).</summary>
    public bool CanFill => Candidate.CanFill && _fill is not null;

    public string FillTip => CanFill
        ? TableEditText.T("파티션 편집기에 이 후보를 채웁니다(실행하지 않음)", "Fill this option into the partition editor (nothing runs)")
        : TableEditText.T("앱 확인에 ✖ 가 있어 채울 수 없습니다", "The app checks found a ✖, so this cannot be filled");

    [ObservableProperty]
    private bool _isFilled;

    /// <summary>「편집기에 채우기」. 채웠으면 true.</summary>
    public bool Fill() => CanFill && _fill!(this);

    [RelayCommand(CanExecute = nameof(CanFill))]
    private void FillEditor() => Fill();

    private static string Describe(PartitionCandidate c)
    {
        var parts = new List<string> { $"{c.Method.ToUpperInvariant()}({string.Join(", ", c.KeyColumns)})" };
        if (!string.IsNullOrEmpty(c.Interval))
        {
            parts.Add("INTERVAL " + c.Interval);
        }

        if (c.Layout.HashCount is { } n)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n}"));
        }

        if (c.Subpartition is { } sub)
        {
            parts.Add(TableEditText.T("하위 ", "sub ") + $"{sub.Method.ToUpperInvariant()}({string.Join(", ", sub.KeyColumns)})");
        }

        if (c.LocalIndexes.Count > 0)
        {
            parts.Add("LOCAL " + string.Join(", ", c.LocalIndexes));
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// AI 대화 창에서 한 차례 결과의 마무리 카드(PA-22): 「파티션 안 함」 권장과 「그래도 후보 3개 받기」(⑤), 「왜 그 열은 안 했나」(not_chosen),
/// 「AI 원문 보기」, 「보고서로 저장…」. 원문을 읽지 못했으면 그 안내를 먼저 보인다(PA-6).
/// </summary>
public sealed partial class AdviceResultCard : ObservableObject
{
    private readonly PartitionAdviceConversation _owner;

    public AdviceResultCard(PartitionAdviceConversation owner, AdvisorOutcome outcome, IReadOnlyList<AdviceCandidateCard> cards)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(cards);
        _owner = owner;
        Outcome = outcome;
        Cards = cards;
        NotChosenLines = [.. (outcome.Advice?.NotChosen ?? []).Select(n => n.Column + " — " + n.Reason)];
    }

    public AdvisorOutcome Outcome { get; }

    /// <summary>이 차례의 후보 카드(보고서에 싣는다).</summary>
    public IReadOnlyList<AdviceCandidateCard> Cards { get; }

    public IReadOnlyList<string> NotChosenLines { get; }

    public bool HasNotChosen => NotChosenLines.Count > 0;

    /// <summary>AI 가 「파티션 안 함」을 권했다(⑤).</summary>
    public bool IsNoPartition => Outcome.Advice is { } a && string.Equals(a.Verdict, AdviceVerdict.NoPartition, StringComparison.OrdinalIgnoreCase);

    /// <summary>「그래도 후보 3개 받기」 단추(파티션 안 함이고 후보가 없을 때).</summary>
    public bool ShowsRequestCandidates => IsNoPartition && Cards.Count == 0;

    /// <summary>응답을 읽지 못함(원문만 보이고 채우기 없음, PA-6).</summary>
    public bool IsUnreadable => Outcome.Advice is null;

    public string UnreadableText => !IsUnreadable ? string.Empty : TableEditText.T(
        "AI 응답을 추천 모양으로 읽지 못했습니다. 원문만 볼 수 있습니다(편집기에 채울 수 없음).",
        "Could not read the AI answer as advice. Only the raw text is available (nothing to fill).");

    public string RawText => Outcome.RawText;

    public bool HasRawText => RawText.Length > 0;

    [ObservableProperty]
    private bool _isRawOpen;

    [ObservableProperty]
    private bool _isNotChosenOpen;

    public bool CanSaveReport => _owner.CanSaveReport;

    /// <summary>이 대화가 지금 대화 창의 대화이고 진행 중이 아니다(「후보 3개 받기」를 켬).</summary>
    public bool CanAsk => _owner.CanAsk;

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task RequestCandidatesAsync() => _owner.RequestCandidatesAsync();

    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private Task SaveReportAsync() => _owner.SaveReportAsync(this);

    /// <summary>대화의 진행 상태가 바뀌었다: 단추 상태를 다시 계산한다.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanAsk));
        RequestCandidatesCommand.NotifyCanExecuteChanged();
    }
}
