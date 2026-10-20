using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Oranda.Perf.Common;

public enum Verdict
{
    /// <summary>목표 이내.</summary>
    Met,

    /// <summary>목표 초과.</summary>
    Exceeded,

    /// <summary>못 쟀다(이유는 <see cref="ReportRow.Note"/>).</summary>
    NotMeasured,
}

/// <summary>결과표 한 줄. 판정 기준 값(<see cref="Judged"/>)은 중앙값·p95·최대 중 PRD 가 정한 쪽이다.</summary>
public sealed record ReportRow(string Item, string Unit, double? Limit, Summary? Summary, string Judged, Verdict Verdict, string Note)
{
    /// <summary>판정 기준 값이 한도 이하이면 Met. 표본이 없으면 NotMeasured. 한도가 null 이면 판정 없이 Met(참고 값).</summary>
    public static ReportRow Create(string item, string unit, double? limit, IReadOnlyCollection<double> samples, string judged = "p95", string note = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        ArgumentNullException.ThrowIfNull(samples);
        if (judged is not ("median" or "p95" or "max"))
        {
            throw new ArgumentException("judged 는 median, p95, max 중 하나여야 한다.", nameof(judged));
        }

        if (samples.Count == 0)
        {
            return new ReportRow(item, unit, limit, null, judged, Verdict.NotMeasured, note);
        }

        var s = Stats.Summarize(samples);
        var basis = judged switch { "median" => s.Median, "max" => s.Max, _ => s.P95 };
        var verdict = limit is null || basis <= limit ? Verdict.Met : Verdict.Exceeded;
        return new ReportRow(item, unit, limit, s, judged, verdict, note);
    }

    public static ReportRow Skipped(string item, string unit, double? limit, string reason) =>
        new(item, unit, limit, null, "p95", Verdict.NotMeasured, reason);
}

/// <summary>측정 한 번의 결과 묶음과 JSON·Markdown 쓰기.</summary>
public sealed class Report(string title, string environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public string Title { get; } = title;

    public string Environment { get; } = environment;

    public List<ReportRow> Rows { get; } = [];

    public void WriteTo(string directory, string baseName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, baseName + ".json"), ToJson());
        File.WriteAllText(Path.Combine(directory, baseName + ".md"), ToMarkdown());
    }

    public string ToJson() => JsonSerializer.Serialize(new { Title, Environment, Utc = DateTime.UtcNow, Rows }, JsonOptions);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Title);
        sb.AppendLine();
        sb.Append("- 환경: ").AppendLine(Environment);
        sb.AppendLine();
        sb.AppendLine("| 항목 | 목표 | n | 중앙값 | p95 | 최대 | 판정 기준 | 판정 | 비고 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in Rows)
        {
            var limit = r.Limit is { } l ? "≤ " + F(l) + " " + r.Unit : "-";
            var verdict = r.Verdict switch { Verdict.Met => "충족", Verdict.Exceeded => "초과", _ => "미측정" };
            if (r.Summary is { } s)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(r.Item)} | {limit} | {s.N} | {F(s.Median)} | {F(s.P95)} | {F(s.Max)} | {r.Judged} | {verdict} | {Cell(r.Note)} |");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(r.Item)} | {limit} | 0 | - | - | - | - | {verdict} | {Cell(r.Note)} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>표 칸 안의 세로줄·줄바꿈이 표를 깨지 않게 한다.</summary>
    private static string Cell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string F(double v) => v.ToString(v >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture);
}
