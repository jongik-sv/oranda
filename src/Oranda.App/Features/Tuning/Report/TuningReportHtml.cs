using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Lexing;
using Oranda.Language.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 튜닝 보고서 HTML 생성기(TSK-06-07). 입력 = 수집 결과(<see cref="TuningReportData"/>), 출력 = 자기 완결 HTML 글 하나.
/// 화면과 떨어진 순수 함수다: 파일·DB·네트워크를 만지지 않는다. 구조·절 순서·스타일은 시안 tuning-report.html 을 따른다
/// (인라인 CSS·SVG, &lt;details&gt; 접기, 목차, 인쇄용, 라이트·다크). 스크립트·외부 자원은 넣지 않는다.
/// 모든 SQL·값은 <see cref="Esc"/> 로 HTML 이스케이프한다. 바인드 값은 <see cref="TuningReportOptions.IncludeBindValues"/> 일 때만 적는다.
/// </summary>
public static partial class TuningReportHtml
{
    /// <summary>보고서 절 순서(목차 번호 = 위치 + 1).</summary>
    public static readonly IReadOnlyList<TuningReportSection> Order =
    [
        TuningReportSection.Summary, TuningReportSection.SqlText, TuningReportSection.Plans, TuningReportSection.Graph,
        TuningReportSection.ExecStats, TuningReportSection.ObjectStats, TuningReportSection.ChildCursors, TuningReportSection.Waits,
        TuningReportSection.Awr, TuningReportSection.Candidates, TuningReportSection.Environment,
    ];

    /// <summary>요약 카드 상한.</summary>
    public const int MaxCards = 6;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>HTML 글·속성 값 이스케이프(&lt; &gt; &amp; " ' 만 — 한글·가운뎃점 등은 UTF-8 그대로 둔다).</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        if (s.AsSpan().IndexOfAny("<>&\"'") < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            _ = c switch
            {
                '<' => sb.Append("&lt;"),
                '>' => sb.Append("&gt;"),
                '&' => sb.Append("&amp;"),
                '"' => sb.Append("&quot;"),
                '\'' => sb.Append("&#39;"),
                _ => sb.Append(c),
            };
        }

        return sb.ToString();
    }

    /// <summary>제안 파일 이름: <c>tuning_{sql_id}_{yyyyMMdd_HHmm}.html</c>.</summary>
    public static string SuggestedFileName(string sqlId, DateTimeOffset at)
    {
        var id = new string((sqlId ?? string.Empty).Trim().Where(char.IsLetterOrDigit).ToArray());
        return string.Create(Inv, $"tuning_{(id.Length == 0 ? "sql" : id)}_{at:yyyyMMdd_HHmm}.html");
    }

    public static string Build(TuningReportData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var w = new Writer(d.Options.English);
        var en = w.En;
        var sqlId = d.Options.SqlId.Trim();
        var title = L(en, "SQL 튜닝 보고서", "SQL tuning report");

        w.Raw("<!doctype html>\n<html lang=\"").Raw(en ? "en" : "ko").Raw("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Raw("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Raw("<meta name=\"generator\" content=\"Oranda ").Text(d.Context.AppVersion).Raw("\">\n")
            .Raw("<title>").Text(title + " " + sqlId).Raw("</title>\n<style>\n").Raw(TuningReportStyles.Css).Raw("</style>\n</head>\n<body>\n<div class=\"wrap\">\n");

        Header(w, d, title, sqlId);
        var states = Order.Select(s => (Section: s, Excluded: ExcludedReason(d, s))).ToList();
        Toc(w, states);
        w.Raw("<main>\n");
        for (var i = 0; i < states.Count; i++)
        {
            var (s, excluded) = states[i];
            Section(w, d, s, i + 1, excluded);
        }

        w.Raw("</main>\n\n<footer>\n")
            .Raw("  <span>").Text(L(en, "모든 자료는 SELECT 로만 모았습니다. DB 에 쓰지 않았습니다.", "All data was read with SELECT only. Nothing was written to the database.")).Raw("</span>\n")
            .Raw("  <span>").Text(L(en, "후보 DDL·재작성 SQL 은 제안이며 실행되지 않았습니다.", "Candidate DDL and rewritten SQL are suggestions and were not run.")).Raw("</span>\n")
            .Raw("  <span>").Text("Oranda " + d.Context.AppVersion + " · " + Stamp(d.Context.CreatedAt)).Raw("</span>\n")
            .Raw("</footer>\n</div>\n</body>\n</html>\n");
        return w.ToString();
    }

    // ---- 머리·목차 ----

    private static void Header(Writer w, TuningReportData d, string title, string sqlId)
    {
        var en = w.En;
        var c = d.Context;
        w.Raw("<header class=\"top\">\n  <div class=\"kicker\">").Text("Oranda · " + title + " · " + L(en, "읽기 전용 수집", "read-only collection")).Raw("</div>\n")
            .Raw("  <h1>").Text(title).Raw(" <code>").Text(sqlId).Raw("</code></h1>\n  <div class=\"meta\">\n");
        w.Raw("    <span>DB <b>").Text(c.DatabaseName ?? "—").Raw("</b> ").Text(c.ServerVersion + (c.IsRac ? " · RAC" : string.Empty)).Raw("</span>\n");
        var (envText, envClass) = c.Environment switch
        {
            ConnectionEnvironment.Production => (L(en, "운영", "Production"), "danger"),
            ConnectionEnvironment.Test => (L(en, "테스트", "Test"), "warn"),
            _ => (L(en, "개발", "Development"), "ok"),
        };
        w.Raw("    <span>").Text(L(en, "접속", "Connection")).Raw(" <b>").Text(c.User + "@" + c.ConnectionName).Raw("</b> <span class=\"chip ").Raw(envClass).Raw("\">").Text(envText).Raw("</span></span>\n");
        w.Raw("    <span>").Text(L(en, "만든 때", "Created")).Raw(" <b>").Text(c.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Raw("</b> (").Text(c.CreatedAt.ToString("zzz", Inv)).Raw(")</span>\n");
        var childText = d.Options.ChildNumber is { } n
            ? "child " + n.ToString(Inv)
            : d.Cursors.Value is { } cur ? string.Format(Inv, L(en, "전체 {0}개", "all {0}"), cur.Children.Count) : L(en, "전체", "all");
        w.Raw("    <span>").Text(L(en, "자식 커서", "Child cursors")).Raw(" <b>").Text(childText).Raw("</b></span>\n");
        w.Raw("    <span>").Text(L(en, "바인드 값", "Bind values")).Raw(" <b>").Text(d.Options.IncludeBindValues ? L(en, "포함", "included") : L(en, "포함 안 함", "not included")).Raw("</b></span>\n");
        w.Raw("    <span>Oranda ").Text(c.AppVersion).Raw("</span>\n  </div>\n</header>\n\n");
    }

    private static void Toc(Writer w, List<(TuningReportSection Section, string? Excluded)> states)
    {
        var en = w.En;
        w.Raw("<nav class=\"toc\" aria-label=\"").Text(L(en, "목차", "Contents")).Raw("\">\n  <div class=\"lbl\">").Text(L(en, "목차", "Contents")).Raw("</div>\n  <ol>\n");
        for (var i = 0; i < states.Count; i++)
        {
            var (s, excluded) = states[i];
            w.Raw(excluded is null ? "    <li>" : "    <li class=\"off\">").Raw("<a href=\"#s").Raw((i + 1).ToString(Inv)).Raw("\">")
                .Text(TuningReportText.SectionTitle(s, en) + (excluded is null ? string.Empty : " (" + L(en, "포함 안 함", "not included") + ")")).Raw("</a></li>\n");
        }

        w.Raw("  </ol>\n</nav>\n\n");
    }

    /// <summary>절이 빠진 이유(고르지 않음·팩 없음). 담기면 null. 수집 실패는 빠짐이 아니다(절 안에 실패를 적는다).</summary>
    internal static string? ExcludedReason(TuningReportData d, TuningReportSection s)
    {
        if (!d.Options.Includes(s))
        {
            return TuningReportText.NotChosen(d.Options.English);
        }

        return s switch
        {
            TuningReportSection.Awr => d.Awr.Excluded,
            TuningReportSection.Waits => d.Waits.Excluded,
            _ => null,
        };
    }

    // ---- 절 ----

    private static void Section(Writer w, TuningReportData d, TuningReportSection s, int number, string? excluded)
    {
        var en = w.En;
        var chips = new StringBuilder();
        var body = new Writer(en);
        if (excluded is not null)
        {
            Chip(chips, L(en, "포함 안 함", "not included"));
            var pack = s == TuningReportSection.Awr && d.Options.Includes(s);
            body.Raw("<div class=\"excluded\">").Raw(pack ? "<span>🔒</span>" : string.Empty).Raw("<div><b>").Text(L(en, "포함 안 함: ", "Not included: ") + excluded).Raw("</b>");
            if (pack)
            {
                body.Raw("<br>").Text(TuningReportText.NoDiagnosticsHint(en));
            }

            body.Raw("</div></div>");
        }
        else
        {
            switch (s)
            {
                case TuningReportSection.Summary: SummarySection(body, chips, d); break;
                case TuningReportSection.SqlText: SqlTextSection(body, chips, d); break;
                case TuningReportSection.Plans: PlansSection(body, chips, d); break;
                case TuningReportSection.Graph: GraphSection(body, chips, d); break;
                case TuningReportSection.ExecStats: ExecStatsSection(body, chips, d); break;
                case TuningReportSection.ObjectStats: ObjectsSection(body, chips, d); break;
                case TuningReportSection.ChildCursors: CursorsSection(body, chips, d); break;
                case TuningReportSection.Waits: WaitsSection(body, chips, d); break;
                case TuningReportSection.Awr: AwrSection(body, chips, d); break;
                case TuningReportSection.Candidates: CandidatesSection(body, chips, d); break;
                case TuningReportSection.Environment: EnvironmentSection(body, chips, d); break;
            }
        }

        var id = "s" + number.ToString(Inv);
        w.Raw("<details class=\"sec\" id=\"").Raw(id).Raw("\" data-section=\"").Raw(s.ToString()).Raw(excluded is null ? "\" open>\n" : "\">\n")
            .Raw("  <summary><span class=\"num\">").Raw(number.ToString(Inv)).Raw("</span><h2>").Text(TuningReportText.SectionTitle(s, en)).Raw("</h2>");
        if (chips.Length > 0)
        {
            w.Raw("<span class=\"right\">").Raw(chips.ToString()).Raw("</span>");
        }

        w.Raw("</summary>\n  <div class=\"body\">\n").Raw(body.ToString()).Raw("\n  </div>\n</details>\n\n");
    }

    private static void Failed(Writer w, string reason) =>
        w.Raw("<div class=\"excluded failed\"><span>⚠</span><div><b>").Text(L(w.En, "수집 실패: ", "Collection failed: ") + reason).Raw("</b></div></div>");

    private static bool FailedPart<T>(Writer w, ReportPart<T> part)
        where T : class
    {
        if (part.Failure is { } f)
        {
            Failed(w, f);
            return true;
        }

        if (part.Excluded is { } e)
        {
            w.Raw("<div class=\"excluded\"><div><b>").Text(L(w.En, "포함 안 함: ", "Not included: ") + e).Raw("</b></div></div>");
            return true;
        }

        return part.Value is null;
    }

    private static void Chip(StringBuilder sb, string text, string? kind = null) =>
        sb.Append("<span class=\"chip").Append(kind is null ? string.Empty : " " + kind).Append("\">").Append(Esc(text)).Append("</span>");

    // 11. 환경
    private static void EnvironmentSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Environment))
        {
            return;
        }

        var rows = d.Environment.Value!;
        foreach (var v in rows.Where(r => r.Kind == TuningEnvKind.Version))
        {
            w.Raw("<p class=\"mono\" style=\"margin:0 0 10px\">").Text(v.Value).Raw("</p>");
        }

        w.Raw("<div class=\"grid2\">\n");
        EnvTable(w, L(en, "optimizer 매개변수", "Optimizer parameters"), rows.Where(r => r.Kind == TuningEnvKind.Parameter));
        EnvTable(w, "NLS", rows.Where(r => r.Kind == TuningEnvKind.Nls));
        w.Raw("</div>");
    }

    private static void EnvTable(Writer w, string head, IEnumerable<TuningEnvRow> rows)
    {
        w.Raw("<div class=\"tw\"><table><thead><tr><th>").Text(head).Raw("</th><th>").Text(L(w.En, "값", "Value")).Raw("</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            w.Raw("<tr><td class=\"mono\">").Text(r.Name).Raw("</td><td class=\"mono\">").Text(r.Value ?? string.Empty).Raw("</td></tr>");
        }

        w.Raw("</tbody></table></div>\n");
    }

    // ---- 값 서식 ----

    internal static string StepLabel(PlanStep s) =>
        s.Operation + (string.IsNullOrEmpty(s.Options) ? string.Empty : " " + s.Options) + (ObjectName(s) is { Length: > 0 } o ? " " + o : string.Empty);

    internal static string ObjectName(PlanStep s) =>
        string.IsNullOrEmpty(s.ObjectName) ? string.Empty : string.IsNullOrEmpty(s.ObjectOwner) ? s.ObjectName : s.ObjectOwner + "." + s.ObjectName;

    private static string AnalyzedAgo(DateTime? at, DateTimeOffset now, bool en)
    {
        if (at is not { } a)
        {
            return L(en, "수집 안 됨", "never");
        }

        var days = (now.DateTime.Date - a.Date).Days;
        return days <= 0 ? L(en, "오늘", "today") : string.Format(Inv, L(en, "{0} 일 전", "{0} days ago"), days);
    }

    internal static string Num(double v) =>
        Math.Abs(v) >= 100 || v == Math.Floor(v) ? Math.Round(v).ToString("N0", Inv) : v.ToString(Math.Abs(v) >= 10 ? "N1" : "N2", Inv);

    internal static string Num(long v) => v.ToString("N0", Inv);

    private static string Opt(long? v) => v is { } x ? x.ToString("N0", Inv) : string.Empty;

    internal static string Pct(double share) => Math.Round(Math.Clamp(share, 0, 1) * 100).ToString("0", Inv);

    private static string Yn(bool v) => v ? "Y" : "N";

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string L(bool en, string ko, string english) => en ? english : ko;

    /// <summary>글 조각을 모으는 도우미: <see cref="Text"/> 는 늘 이스케이프, <see cref="Raw"/> 는 생성기가 만든 태그만.</summary>
    private sealed class Writer(bool en)
    {
        private readonly StringBuilder _sb = new(64 * 1024);

        public bool En { get; } = en;

        public Writer Raw(string s)
        {
            _sb.Append(s);
            return this;
        }

        public Writer Text(string? s)
        {
            _sb.Append(Esc(s));
            return this;
        }

        public override string ToString() => _sb.ToString();
    }
}
