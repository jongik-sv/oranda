using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>근거 패널의 열 한 줄(이름 · 형식 · 고유값 · 쓰임).</summary>
public sealed record EvidenceColumnLine(string Name, string DataType, string Distinct, string Usage);

/// <summary>근거 패널의 분포 하나(제목 + 버킷 줄).</summary>
public sealed record EvidenceDistributionView(string Title, IReadOnlyList<string> Lines);

/// <summary>앱 확인 한 줄(✔/⚠/✖ + 문구).</summary>
public sealed record AdviceCheckLine(AdviceCheckLevel Level, string Mark, string Text)
{
    public bool IsStop => Level == AdviceCheckLevel.Stop;

    public bool IsWarn => Level == AdviceCheckLevel.Warn;
}

/// <summary>후보 카드 하나(시안 ③ 오른쪽).</summary>
public sealed partial class AdviceCandidateCard : ObservableObject
{
    public AdviceCandidateCard(AdvisorCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
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
    public bool CanFill => Candidate.CanFill;

    public string FillTip => CanFill
        ? TableEditText.T("파티션 편집기에 이 후보를 채웁니다(실행하지 않음)", "Fill this option into the partition editor (nothing runs)")
        : TableEditText.T("앱 확인에 ✖ 가 있어 채울 수 없습니다", "The app checks found a ✖, so this cannot be filled");

    [ObservableProperty]
    private bool _isFilled;

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

/// <summary>결과 창이 부르는 동작(객체 상세가 넣는다).</summary>
public sealed record AdvisorResultHost
{
    /// <summary>「편집기에 채우기」. 채웠으면 true.</summary>
    public Func<AdvisorCandidate, bool>? Fill { get; init; }

    /// <summary>같은 근거로 다시 받기(true = 후보를 꼭 내라고 덧붙임, ⑤). 취소·실패는 예외 또는 Error.</summary>
    public Func<AdvicePromptOptions, CancellationToken, Task<AdvisorOutcome>>? Ask { get; init; }

    /// <summary>보고서 파일 고르기(제안 이름 → 경로, 취소 = null).</summary>
    public Func<string, CancellationToken, Task<string?>>? PickReportFile { get; init; }
}

/// <summary>
/// AI 파티션 추천 결과 창(설계 3장 ③·⑤, 비모달, 탭 하나에 하나). 왼쪽 근거 패널 = 앱이 모은 숫자, 오른쪽 = 후보 카드(AI 이유 + 앱 확인).
/// 「분포 보기」(PA-9)는 같은 창을 근거만으로 연다(<see cref="IsEvidenceOnly"/>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "취소 토큰은 한 번의 작업 동안만 쥐고 끝나면 finally 에서 정리한다(창이 닫히면 Stop/Abort 로 취소).")]
public sealed partial class PartitionAdvisorResultViewModel : ObservableObject
{
    private readonly AdvisorResultHost _host;
    private CancellationTokenSource? _cts;

    /// <summary>근거만(분포 보기).</summary>
    public PartitionAdvisorResultViewModel(PartitionEvidence evidence, AdvisorResultHost host, string? recipient = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        Evidence = evidence;
        Recipient = recipient;
        BuildEvidence(evidence, null, sendValues: true);
    }

    /// <summary>AI 결과.</summary>
    public PartitionAdvisorResultViewModel(AdvisorOutcome outcome, AdvisorResultHost host, string recipient)
        : this((outcome ?? throw new ArgumentNullException(nameof(outcome))).Evidence, host, recipient)
    {
        Load(outcome);
    }

    public PartitionEvidence Evidence { get; private set; }

    public string? Recipient { get; }

    /// <summary>지금 보이는 결과(근거만이면 null).</summary>
    public AdvisorOutcome? Outcome { get; private set; }

    public bool IsEvidenceOnly => Outcome is null;

    public bool HasAi => Outcome is not null;

    public string Title
    {
        get
        {
            var rows = Evidence.NumRows is { } n ? TableEditText.T($" · {n:N0}행", $" · {n:N0} rows") : string.Empty;
            var head = IsEvidenceOnly ? TableEditText.T("파티션 근거 — ", "Partition evidence — ") : TableEditText.T("AI 파티션 추천 — ", "AI partition advice — ");
            var to = Recipient is { Length: > 0 } r && !IsEvidenceOnly ? TableEditText.T(" · 받은 곳 ", " · from ") + r : string.Empty;
            return head + Evidence.Table + rows + to;
        }
    }

    // ---- 근거 패널 --------------------------------------------------------------

    public ObservableCollection<EvidenceColumnLine> Columns { get; } = [];

    public ObservableCollection<EvidenceDistributionView> Distributions { get; } = [];

    public ObservableCollection<string> IndexLines { get; } = [];

    public ObservableCollection<string> SkippedLines { get; } = [];

    public bool HasSkipped => SkippedLines.Count > 0;

    /// <summary>통계 줄(행 수 · 블록 · 마지막 수집일).</summary>
    public string StatsText
    {
        get
        {
            var parts = new List<string>();
            if (Evidence.NumRows is { } rows)
            {
                parts.Add(TableEditText.T($"행 {rows:N0}", $"{rows:N0} rows"));
            }

            if (Evidence.Blocks is { } blocks)
            {
                parts.Add(TableEditText.T($"블록 {blocks:N0}", $"{blocks:N0} blocks"));
            }

            if (Evidence.LastAnalyzed is { } at)
            {
                var days = (int)Math.Floor((Evidence.CollectedAt - at).TotalDays);
                parts.Add(TableEditText.T($"통계 수집 {at:yyyy-MM-dd}({days}일 전)", $"statistics {at:yyyy-MM-dd} ({days} days ago)"));
            }

            return parts.Count == 0 ? TableEditText.T("통계 없음", "No statistics") : string.Join(" · ", parts);
        }
    }

    // ---- AI 결과 ---------------------------------------------------------------

    public ObservableCollection<AdviceCandidateCard> Cards { get; } = [];

    public ObservableCollection<string> NotChosenLines { get; } = [];

    public bool HasNotChosen => NotChosenLines.Count > 0;

    /// <summary>AI 가 「파티션 안 함」을 권했다(⑤).</summary>
    public bool IsNoPartition => Outcome?.Advice is { } a && string.Equals(a.Verdict, AdviceVerdict.NoPartition, StringComparison.OrdinalIgnoreCase);

    /// <summary>「후보 3개 받기」 단추(⑤, 파티션 안 함이고 후보가 없을 때).</summary>
    public bool ShowsRequestCandidates => IsNoPartition && Cards.Count == 0 && _host.Ask is not null;

    public string Summary => Outcome?.Advice?.Summary ?? string.Empty;

    public bool HasSummary => Summary.Length > 0;

    /// <summary>응답을 읽지 못함(원문만 보이고 채우기 끔, PA-6).</summary>
    public bool IsUnreadable => Outcome is { Advice: null };

    public string UnreadableText => Outcome?.Error is { } error
        ? TableEditText.T("AI 가 실패했습니다: ", "The AI failed: ") + error
        : TableEditText.T("AI 응답을 추천 모양으로 읽지 못했습니다. 아래 원문만 보입니다(편집기에 채울 수 없음).", "Could not read the AI answer as advice. Only the raw text is shown (nothing to fill).");

    public string RawText => Outcome?.RawText ?? string.Empty;

    public bool HasRawText => RawText.Length > 0;

    [ObservableProperty]
    private bool _isRawOpen;

    [ObservableProperty]
    private bool _isNotChosenOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAsk))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool CanAsk => !IsBusy && _host.Ask is not null && Outcome is not null;

    public bool CanSaveReport => _host.PickReportFile is not null;

    /// <summary>결과를 바꿔 넣는다(다시 받기).</summary>
    public void Load(AdvisorOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
        Evidence = outcome.Evidence;
        BuildEvidence(outcome.Evidence, outcome.Vault, outcome.Options.SendValues);
        Cards.Clear();
        foreach (var c in outcome.Candidates)
        {
            Cards.Add(new AdviceCandidateCard(c));
        }

        NotChosenLines.Clear();
        foreach (var n in outcome.Advice?.NotChosen ?? [])
        {
            NotChosenLines.Add(n.Column + " — " + n.Reason);
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>「편집기에 채우기」.</summary>
    public bool Fill(AdviceCandidateCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.CanFill || _host.Fill is not { } fill)
        {
            return false;
        }

        if (!fill(card.Candidate))
        {
            StatusText = TableEditText.T("편집기에 채우지 못했습니다(편집을 끄거나 파티션을 고칠 수 없는 상태)", "Could not fill the editor (editing is off or partitions cannot be edited)");
            return false;
        }

        foreach (var other in Cards)
        {
            other.IsFilled = ReferenceEquals(other, card);
        }

        StatusText = TableEditText.T($"{card.Candidate.Candidate.Rank}안을 파티션 편집기에 채웠습니다. DDL 미리보기를 확인하세요.",
            $"Filled option {card.Candidate.Candidate.Rank} into the partition editor. Check the DDL preview.");
        return true;
    }

    /// <summary>「다시 받기」(같은 근거·같은 선택).</summary>
    public Task RetryAsync() => AskAgainAsync(requireCandidates: false);

    /// <summary>⑤ 「후보 3개 받기」(같은 근거, 후보를 꼭 내라고 덧붙임).</summary>
    public Task RequestCandidatesAsync() => AskAgainAsync(requireCandidates: true);

    /// <summary>진행 중인 다시 받기를 멈춘다.</summary>
    public void Stop() => _cts?.Cancel();

    private async Task AskAgainAsync(bool requireCandidates)
    {
        if (!CanAsk || Outcome is not { } last)
        {
            return;
        }

        IsBusy = true;
        StatusText = TableEditText.T("AI 응답을 기다리는 중…", "Waiting for the AI…");
        _cts = new CancellationTokenSource();
        try
        {
            var outcome = await _host.Ask!(last.Options with { RequireCandidates = requireCandidates }, _cts.Token);
            Load(outcome);
            StatusText = outcome.Error is { } error && outcome.Advice is null
                ? TableEditText.T("AI 가 답하지 못했습니다: ", "The AI did not answer: ") + error
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusText = TableEditText.T("멈췄습니다.", "Stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = TableEditText.T("다시 받지 못했습니다: ", "Could not ask again: ") + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>「보고서로 저장」(HTML). 저장한 경로.</summary>
    public async Task<string?> SaveReportAsync(CancellationToken ct = default)
    {
        if (_host.PickReportFile is not { } pick)
        {
            return null;
        }

        var path = await pick(Evidence.Table + "_partition_advice.html", ct);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            await File.WriteAllTextAsync(path, PartitionAdviceReport.Render(this, DateTime.Now), new System.Text.UTF8Encoding(false), ct);
            StatusText = TableEditText.T("보고서를 저장했습니다: ", "Saved the report: ") + path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = TableEditText.T("보고서를 저장하지 못했습니다: ", "Could not save the report: ") + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 근거 패널을 채운다. 값을 가려 보냈으면 상위 값 옆에 그 자리표시(<c>V1</c>)를 붙여 AI 이유의 자리표시와 맞춰 볼 수 있게 한다
    /// (값 자체는 이 PC 화면에만 있다).
    /// </summary>
    private void BuildEvidence(PartitionEvidence e, EvidenceValueVault? vault, bool sendValues)
    {
        Columns.Clear();
        var pk = e.Keys.Where(k => k.Kind == EvidenceKeyKind.Primary).SelectMany(k => k.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in e.Columns)
        {
            var usage = new List<string>();
            if (pk.Contains(c.Name))
            {
                usage.Add("PK");
            }

            if (e.Predicates.FirstOrDefault(p => string.Equals(p.Column, c.Name, StringComparison.OrdinalIgnoreCase)) is { } use)
            {
                usage.Add(TableEditText.T("조건 ", "predicate ") + Percent(use.Share));
            }

            if (c.NumNulls is > 0 && e.NumRows is > 0)
            {
                usage.Add("NULL " + Percent((double)c.NumNulls.Value / e.NumRows.Value));
            }

            if (c.DateLike is { } d)
            {
                usage.Add(TableEditText.T("날짜형 ", "date-like ") + Format(d.Format) + " " + Percent(d.MatchRatio));
            }

            Columns.Add(new EvidenceColumnLine(c.Name, c.DataType, c.NumDistinct is { } n ? Compact(n) : "—", string.Join(" · ", usage)));
        }

        Distributions.Clear();
        foreach (var d in e.Distributions)
        {
            var total = Math.Max(1, d.Buckets.Sum(b => b.Count) + d.RestCount);
            var lines = d.Buckets.Select(b =>
            {
                var label = d.Kind == DistributionKind.TopValues && !sendValues && vault is not null
                    ? b.Label + " (" + vault.Mask(d.Column, b.Label) + ")"
                    : b.Label;
                return string.Create(CultureInfo.InvariantCulture, $"{label}  {b.Count:N0}  {Percent((double)b.Count / total)}");
            }).ToList();
            if (d.RestCount > 0)
            {
                lines.Add(TableEditText.T("그 밖 ", "rest ") + string.Create(CultureInfo.InvariantCulture, $"{d.RestCount:N0}  {Percent((double)d.RestCount / total)}"));
            }

            if (d.NullCount is > 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"NULL  {d.NullCount:N0}"));
            }

            var kind = d.Kind == DistributionKind.Monthly ? TableEditText.T("월별 건수", "rows per month") : TableEditText.T("값별 건수", "rows per value");
            var sample = d.SamplePercent is { } p
                ? string.Create(CultureInfo.InvariantCulture, $" — SAMPLE({p:0.##}), {d.SampledRows:N0}")
                : string.Create(CultureInfo.InvariantCulture, $" — {d.SampledRows:N0}");
            Distributions.Add(new EvidenceDistributionView(d.Column + " " + kind + sample, lines));
        }

        IndexLines.Clear();
        foreach (var i in e.Indexes)
        {
            var mark = i.PrimaryKey ? " PK" : i.Unique ? " UNIQUE" : string.Empty;
            IndexLines.Add($"{i.Name} ({string.Join(", ", i.Columns)}){mark}");
        }

        SkippedLines.Clear();
        foreach (var s in e.Skipped)
        {
            SkippedLines.Add(PartitionAdvisorText.Skip(s));
        }
    }

    internal static string Percent(double share) => (share * 100).ToString(share < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static string Compact(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    private static string Format(DateLikeFormat f) => f switch
    {
        DateLikeFormat.Yyyymmdd => "YYYYMMDD",
        DateLikeFormat.Yyyymm => "YYYYMM",
        _ => "YYYY-MM-DD",
    };
}
