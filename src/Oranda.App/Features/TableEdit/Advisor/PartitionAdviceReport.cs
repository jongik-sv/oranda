using System.Globalization;
using System.Net;
using System.Text;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>
/// 「보고서로 저장」 HTML(설계 3장 ③). AI 튜닝 레포트와 같은 꼴: 파일 하나로 완결(외부 CSS·JS·글꼴 없음, 스크립트 없음 — CSP 메타로 한 번 더 막음),
/// 라이트·다크·인쇄. AI·DB 에서 온 글은 모두 HTML 이스케이프한다. 근거 패널 = 화면과 같은 글(값을 가려 보냈으면 상위 값 옆에 자리표시).
/// </summary>
public static class PartitionAdviceReport
{
    private const string Css = """
        :root{color-scheme:light dark;--fg:#1f2328;--muted:#59636e;--bg:#fff;--line:#d1d9e0;--ok:#1a7f37;--warn:#9a6700;--stop:#cf222e;--card:#f6f8fa}
        @media (prefers-color-scheme:dark){:root{--fg:#e6edf3;--muted:#9198a1;--bg:#0d1117;--line:#3d444d;--ok:#3fb950;--warn:#d29922;--stop:#f85149;--card:#151b23}}
        body{font:14px/1.5 system-ui,sans-serif;color:var(--fg);background:var(--bg);margin:0 auto;max-width:960px;padding:16px}
        h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:20px 0 8px;border-bottom:1px solid var(--line)}h3{font-size:14px;margin:12px 0 4px}
        .meta,.note{color:var(--muted)}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:2px 6px;text-align:left;vertical-align:top}
        .card{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:8px 12px;margin:8px 0}
        .ok{color:var(--ok)}.warn{color:var(--warn)}.stop{color:var(--stop)}pre{white-space:pre-wrap;word-break:break-word;background:var(--card);padding:8px;border-radius:4px}
        @media print{body{max-width:none}.card{break-inside:avoid}}
        """;

    public static string Render(PartitionAdvisorResultViewModel vm, DateTime savedAt)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"").Append(TableEditText.T("ko", "en")).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n");
        sb.Append("<title>").Append(H(vm.Title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n");
        sb.Append("<h1>").Append(H(vm.Title)).Append("</h1>\n<p class=\"meta\">");
        sb.Append(H($"{vm.Evidence.Owner}.{vm.Evidence.Table} · {vm.Evidence.Dbms}"));
        if (vm.Evidence.ServerVersion is { } version)
        {
            sb.Append(H(" " + version));
        }

        sb.Append(H(TableEditText.T(" · 근거 모은 때 ", " · evidence collected ") + vm.Evidence.CollectedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (vm.Outcome is { } outcome)
        {
            sb.Append(H(TableEditText.T(" · 받은 때 ", " · received ") + outcome.ReceivedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }

        sb.Append(H(TableEditText.T(" · 저장 ", " · saved ") + savedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append("</p>\n");
        if (vm.Outcome is { Options.SendValues: false })
        {
            sb.Append("<p class=\"note\">").Append(H(TableEditText.T(
                "실제 값은 AI 에 보내지 않았습니다(V1, V2… 자리표시와 건수만). 아래 근거의 괄호 안이 그 자리표시입니다.",
                "Real values were not sent to the AI (V1, V2… placeholders with counts only). The placeholders are shown in brackets below."))).Append("</p>\n");
        }

        sb.Append("<p class=\"note\">").Append(H(TableEditText.T("AI 는 후보만 냅니다. DB 에는 아무것도 실행하지 않았습니다.", "The AI only suggests. Nothing was run on the database."))).Append("</p>\n");

        AppendAdvice(sb, vm);
        AppendEvidence(sb, vm);

        if (vm.HasRawText)
        {
            sb.Append("<h2>").Append(H(TableEditText.T("AI 원문", "Raw AI answer"))).Append("</h2>\n<pre>").Append(H(vm.RawText)).Append("</pre>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendAdvice(StringBuilder sb, PartitionAdvisorResultViewModel vm)
    {
        if (vm.Outcome is null)
        {
            return;
        }

        sb.Append("<h2>").Append(H(TableEditText.T("추천", "Advice"))).Append("</h2>\n");
        if (vm.IsUnreadable)
        {
            sb.Append("<p class=\"stop\">").Append(H(vm.UnreadableText)).Append("</p>\n");
        }

        if (vm.IsNoPartition)
        {
            sb.Append("<p><strong>").Append(H(TableEditText.T("파티션 안 함 권장", "No partitioning recommended"))).Append("</strong></p>\n");
        }

        if (vm.HasSummary)
        {
            sb.Append("<p>").Append(H(vm.Summary)).Append("</p>\n");
        }

        foreach (var card in vm.Cards)
        {
            sb.Append("<div class=\"card\">\n<h3>").Append(H(card.Header)).Append("</h3>\n<p class=\"meta\">").Append(H(card.Shape)).Append("</p>\n");
            AppendList(sb, TableEditText.T("AI 이유", "AI reasons"), card.Reasons, null);
            AppendList(sb, TableEditText.T("주의", "Cautions"), card.Cautions, null);
            if (card.Checks.Count > 0)
            {
                sb.Append("<p><strong>").Append(H(TableEditText.T("앱 확인", "App checks"))).Append("</strong></p>\n<ul>");
                foreach (var check in card.Checks)
                {
                    var css = check.IsStop ? "stop" : check.IsWarn ? "warn" : "ok";
                    sb.Append("<li class=\"").Append(css).Append("\">").Append(H(check.Mark + " " + check.Text)).Append("</li>");
                }

                sb.Append("</ul>\n");
            }

            sb.Append("</div>\n");
        }

        if (vm.HasNotChosen)
        {
            AppendList(sb, TableEditText.T("키로 안 고른 열", "Columns not chosen"), vm.NotChosenLines, "h3");
        }
    }

    private static void AppendEvidence(StringBuilder sb, PartitionAdvisorResultViewModel vm)
    {
        sb.Append("<h2>").Append(H(TableEditText.T("근거 (앱이 모음)", "Evidence (collected by the app)"))).Append("</h2>\n<p>").Append(H(vm.StatsText)).Append("</p>\n");
        if (vm.Columns.Count > 0)
        {
            sb.Append("<table><thead><tr><th>").Append(H(TableEditText.T("열", "Column"))).Append("</th><th>").Append(H(TableEditText.T("형식", "Type")))
                .Append("</th><th>").Append(H(TableEditText.T("고유값", "Distinct"))).Append("</th><th>").Append(H(TableEditText.T("쓰임", "Usage"))).Append("</th></tr></thead><tbody>");
            foreach (var c in vm.Columns)
            {
                sb.Append("<tr><td>").Append(H(c.Name)).Append("</td><td>").Append(H(c.DataType)).Append("</td><td>").Append(H(c.Distinct)).Append("</td><td>").Append(H(c.Usage)).Append("</td></tr>");
            }

            sb.Append("</tbody></table>\n");
        }

        foreach (var d in vm.Distributions)
        {
            AppendList(sb, d.Title, d.Lines, "h3");
        }

        AppendList(sb, TableEditText.T("인덱스", "Indexes"), vm.IndexLines, "h3");
        AppendList(sb, TableEditText.T("건너뛴 항목", "Skipped"), vm.SkippedLines, "h3");
    }

    private static void AppendList(StringBuilder sb, string heading, IEnumerable<string> items, string? tag)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.Append(tag is null ? "<p><strong>" : "<" + tag + ">").Append(H(heading)).Append(tag is null ? "</strong></p>" : "</" + tag + ">").Append("\n<ul>");
        foreach (var item in list)
        {
            sb.Append("<li>").Append(H(item)).Append("</li>");
        }

        sb.Append("</ul>\n");
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
}
