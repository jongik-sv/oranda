using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>근거 패널의 열 한 줄(이름 · 형식 · 고유값 · 쓰임).</summary>
public sealed record EvidenceColumnLine(string Name, string DataType, string Distinct, string Usage);

/// <summary>근거 패널의 분포 하나(제목 + 버킷 줄).</summary>
public sealed record EvidenceDistributionView(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// 근거 패널(앱이 모은 숫자, PA-9 「분포 보기」 창과 보고서가 같이 쓴다). AI 추천 자체는 AI 대화 창에 있다(PA-22).
/// 값을 가려 보냈으면 상위 값 옆에 그 자리표시(<c>V1</c>)를 붙여 AI 이유의 자리표시와 맞춰 볼 수 있게 한다(값 자체는 이 PC 화면에만 있다).
/// </summary>
public sealed partial class PartitionEvidenceViewModel : ObservableObject
{
    public PartitionEvidenceViewModel(PartitionEvidence evidence, EvidenceValueVault? vault = null, bool sendValues = true)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Evidence = evidence;
        Build(evidence, vault, sendValues);
    }

    public PartitionEvidence Evidence { get; }

    /// <summary>보고서 파일 고르기(제안 이름 → 경로, 취소 = null). 없으면 「보고서로 저장」을 끈다.</summary>
    public Func<string, CancellationToken, Task<string?>>? PickReportFile { get; init; }

    public string Title
    {
        get
        {
            var rows = Evidence.NumRows is { } n ? TableEditText.T($" · {n:N0}행", $" · {n:N0} rows") : string.Empty;
            return TableEditText.T("파티션 근거 — ", "Partition evidence — ") + Evidence.Table + rows;
        }
    }

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

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool CanSaveReport => PickReportFile is not null;

    /// <summary>「보고서로 저장」(HTML, 근거만). 저장한 경로.</summary>
    public async Task<string?> SaveReportAsync(CancellationToken ct = default)
    {
        if (PickReportFile is not { } pick)
        {
            return null;
        }

        var (path, error) = await PartitionAdviceReport.SaveAsync(pick, Evidence.Table, () => PartitionAdviceReport.Render(Title, this, null, [], DateTime.Now), ct);
        StatusText = error is not null
            ? TableEditText.T("보고서를 저장하지 못했습니다: ", "Could not save the report: ") + error
            : path is not null ? TableEditText.T("보고서를 저장했습니다: ", "Saved the report: ") + path : string.Empty;
        return path;
    }

    private void Build(PartitionEvidence e, EvidenceValueVault? vault, bool sendValues)
    {
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

        foreach (var i in e.Indexes)
        {
            var mark = i.PrimaryKey ? " PK" : i.Unique ? " UNIQUE" : string.Empty;
            IndexLines.Add($"{i.Name} ({string.Join(", ", i.Columns)}){mark}");
        }

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
