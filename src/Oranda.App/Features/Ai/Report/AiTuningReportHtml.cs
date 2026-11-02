using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Chat;
using static Oranda.App.Features.Ai.Report.AiTuningReportBuilder;

namespace Oranda.App.Features.Ai.Report;

/// <summary>
/// 튜닝 레포트의 HTML 생성기. 파일 하나로 완결한다: 외부 CSS·JS·글꼴·이미지가 없고 스크립트도 없다(CSP 메타로 한 번 더 막는다).
/// 라이트·다크(<c>prefers-color-scheme</c>)와 인쇄를 지원한다. AI·SQL 에서 온 글은 모두 HTML 이스케이프한다:
/// AI 글의 마크다운은 <see cref="MarkdownParser"/> 가 읽은 블록·토막을 우리 태그로만 그리므로 응답이 태그·속성을 넣을 수 없다.
/// </summary>
internal static partial class AiTuningReportHtml
{
    public static string Render(AiTuningReportInput input)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n");
        sb.Append("<title>").Append(H(OneLine(input.Title))).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n");
        sb.Append("<header>\n<h1>").Append(H(OneLine(input.Title))).Append("</h1>\n");
        if (input.LiteralsMasked)
        {
            sb.Append("<p class=\"note masked\">").Append(H(MaskedNote)).Append("</p>\n");
        }

        sb.Append("<dl class=\"meta\">");
        foreach (var (name, value) in Meta(input))
        {
            sb.Append("<dt>").Append(H(name)).Append("</dt><dd>").Append(H(value)).Append("</dd>");
        }

        sb.Append("</dl>\n</header>\n<main>\n");

        if (!string.IsNullOrWhiteSpace(input.Result.Summary))
        {
            sb.Append("<section class=\"conclusion\">\n<h2>").Append(ConclusionHeading).Append("</h2>\n");
            AppendMarkdown(sb, input.Result.Summary, 3);
            sb.Append("</section>\n");
        }

        if (!string.IsNullOrWhiteSpace(input.TargetSql))
        {
            sb.Append("<section>\n<h2>").Append(TargetHeading).Append("</h2>\n");
            AppendCode(sb, "sql", input.TargetSql);
            sb.Append("</section>\n");
        }

        if (input.Result.Rewrites.Count > 0)
        {
            sb.Append("<section>\n<h2>").Append(RewritesHeading).Append("</h2>\n");
            for (var i = 0; i < input.Result.Rewrites.Count; i++)
            {
                var rewrite = input.Result.Rewrites[i];
                sb.Append("<article class=\"card\">\n<h3>").Append(Number(i)).Append(". ").Append(H(OneLine(rewrite.Label))).Append("</h3>\n<ul class=\"facts\">");
                sb.Append("<li><b>근거</b> ").Append(H(OneLine(rewrite.Rationale))).Append("</li>");
                if (!string.IsNullOrWhiteSpace(rewrite.Benchmark))
                {
                    sb.Append("<li><b>벤치마크</b> ").Append(H(OneLine(rewrite.Benchmark))).Append("</li>");
                }

                sb.Append("</ul>\n");
                AppendCode(sb, "sql", rewrite.Sql);
                sb.Append("</article>\n");
            }

            sb.Append("</section>\n");
        }

        if (input.Result.IndexSuggestions.Count > 0)
        {
            sb.Append("<section>\n<h2>").Append(IndexesHeading).Append("</h2>\n<p class=\"note\">").Append(H(IndexNote)).Append("</p>\n");
            for (var i = 0; i < input.Result.IndexSuggestions.Count; i++)
            {
                var index = input.Result.IndexSuggestions[i];
                sb.Append("<article class=\"card\">\n<h3>").Append(Number(i)).Append(". ").Append(H(OneLine(IndexTitle(index)))).Append(" <span class=\"badge\">실행 안 함</span></h3>\n<ul class=\"facts\">");
                sb.Append("<li><b>근거</b> ").Append(H(OneLine(index.Rationale))).Append("</li>");
                if (!string.IsNullOrWhiteSpace(index.Risk))
                {
                    sb.Append("<li><b>위험</b> ").Append(H(OneLine(index.Risk))).Append("</li>");
                }

                sb.Append("</ul>\n");
                AppendCode(sb, "sql", index.Ddl);
                sb.Append("</article>\n");
            }

            sb.Append("</section>\n");
        }

        if (input.FollowUps.Count > 0)
        {
            sb.Append("<section>\n<h2>").Append(FollowUpsHeading).Append("</h2>\n");
            for (var i = 0; i < input.FollowUps.Count; i++)
            {
                var followUp = input.FollowUps[i];
                sb.Append("<article class=\"card\">\n<h3>질문 ").Append(Number(i)).Append("</h3>\n<blockquote><p>")
                    .Append(H(followUp.Question.ReplaceLineEndings("\n")).Replace("\n", "<br>\n", StringComparison.Ordinal)).Append("</p></blockquote>\n");
                if (string.IsNullOrWhiteSpace(followUp.Answer))
                {
                    sb.Append("<p class=\"muted\">(답이 없습니다)</p>\n");
                }
                else
                {
                    AppendMarkdown(sb, followUp.Answer, 4);
                }

                sb.Append("</article>\n");
            }

            sb.Append("</section>\n");
        }

        if (input.Tools.Count > 0)
        {
            sb.Append("<section>\n<h2>").Append(ToolsHeading).Append("</h2>\n<div class=\"scroll\"><table>\n<thead><tr><th>도구</th><th>판단</th><th class=\"num\">크기</th><th class=\"num\">걸린 시간</th></tr></thead>\n<tbody>\n");
            foreach (var tool in input.Tools)
            {
                sb.Append("<tr><td>").Append(H(OneLine(tool.Name))).Append("</td><td>").Append(H(OneLine(tool.Verdict)))
                    .Append("</td><td class=\"num\">").Append(H(OneLine(tool.Size))).Append("</td><td class=\"num\">")
                    .Append(H(string.IsNullOrEmpty(tool.Elapsed) ? "-" : OneLine(tool.Elapsed))).Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table></div>\n</section>\n");
        }

        sb.Append("</main>\n<footer>Oranda AI 튜닝 레포트</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static void AppendCode(StringBuilder sb, string language, string code)
    {
        var body = (code ?? string.Empty).ReplaceLineEndings("\n").Trim('\n');
        var cls = language.Length > 0 && SafeLanguage().IsMatch(language) ? " class=\"language-" + language + "\"" : string.Empty;
        sb.Append("<pre><code").Append(cls).Append('>').Append(H(body)).Append("</code></pre>\n");
    }

    /// <summary>AI 가 보낸 마크다운을 블록·토막 단위로 읽어 우리 태그로만 그린다. 글은 모두 이스케이프한다.</summary>
    private static void AppendMarkdown(StringBuilder sb, string markdown, int firstHeadingLevel)
    {
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            AppendBlock(sb, block, firstHeadingLevel);
        }
    }

    private static void AppendBlock(StringBuilder sb, MarkdownBlock block, int firstHeadingLevel)
    {
        switch (block)
        {
            case MarkdownHeading heading:
                // 절 제목(h2) 아래에 오도록 한 단계씩 내린다. 마크다운 제목 1~3 → firstHeadingLevel 부터.
                var level = Math.Min(6, firstHeadingLevel + Math.Clamp(heading.Level, 1, 3) - 1);
                sb.Append("<h").Append(level.ToString(CultureInfo.InvariantCulture)).Append('>');
                AppendSpans(sb, heading.Spans);
                sb.Append("</h").Append(level.ToString(CultureInfo.InvariantCulture)).Append(">\n");
                break;
            case MarkdownParagraph paragraph:
                sb.Append("<p>");
                AppendSpans(sb, paragraph.Spans);
                sb.Append("</p>\n");
                break;
            case MarkdownCode code:
                AppendCode(sb, code.Language.Trim(), code.Code);
                break;
            case MarkdownQuote quote:
                sb.Append("<blockquote>\n");
                foreach (var inner in quote.Blocks)
                {
                    AppendBlock(sb, inner, firstHeadingLevel);
                }

                sb.Append("</blockquote>\n");
                break;
            case MarkdownRule:
                sb.Append("<hr>\n");
                break;
            case MarkdownList list:
                sb.Append("<ul class=\"md-list\">\n");
                foreach (var item in list.Items)
                {
                    sb.Append("<li class=\"d").Append(Math.Clamp(item.Depth, 0, 5).ToString(CultureInfo.InvariantCulture)).Append("\"><span class=\"mk\">")
                        .Append(H(item.Marker)).Append("</span> ");
                    AppendSpans(sb, item.Spans);
                    sb.Append("</li>\n");
                }

                sb.Append("</ul>\n");
                break;
            case MarkdownTable table:
                AppendTable(sb, table);
                break;
        }
    }

    private static void AppendTable(StringBuilder sb, MarkdownTable table)
    {
        static string Align(IReadOnlyList<MarkdownAlign> aligns, int column) =>
            column < aligns.Count && aligns[column] != MarkdownAlign.Left
                ? " class=\"" + (aligns[column] == MarkdownAlign.Right ? "num" : "mid") + "\""
                : string.Empty;

        sb.Append("<div class=\"scroll\"><table>\n<thead><tr>");
        for (var c = 0; c < table.Header.Count; c++)
        {
            sb.Append("<th").Append(Align(table.Aligns, c)).Append('>');
            AppendSpans(sb, table.Header[c]);
            sb.Append("</th>");
        }

        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in table.Rows)
        {
            sb.Append("<tr>");
            for (var c = 0; c < row.Count; c++)
            {
                sb.Append("<td").Append(Align(table.Aligns, c)).Append('>');
                AppendSpans(sb, row[c]);
                sb.Append("</td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table></div>\n");
    }

    private static void AppendSpans(StringBuilder sb, IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var span in spans)
        {
            var text = H(span.Text).Replace("\n", "<br>\n", StringComparison.Ordinal);
            if (span.Style.HasFlag(MarkdownStyle.Code))
            {
                sb.Append("<code>").Append(text).Append("</code>");
                continue;
            }

            // 링크는 주소를 싣지 않는다(외부 참조 없음): 글만 밑줄로 보인다.
            var tags = new List<string>();
            if (span.Style.HasFlag(MarkdownStyle.Bold))
            {
                tags.Add("strong");
            }

            if (span.Style.HasFlag(MarkdownStyle.Italic))
            {
                tags.Add("em");
            }

            if (span.Style.HasFlag(MarkdownStyle.Link))
            {
                tags.Add("u");
            }

            foreach (var tag in tags)
            {
                sb.Append('<').Append(tag).Append('>');
            }

            sb.Append(text);
            foreach (var tag in Enumerable.Reverse(tags))
            {
                sb.Append("</").Append(tag).Append('>');
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_+-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeLanguage();

    private const string Css = """

        :root{color-scheme:light dark;--bg:#ffffff;--fg:#1f2328;--muted:#59636e;--line:#d1d9e0;--panel:#f6f8fa;--accent:#c74634;--ok:#1a7f37;--warn:#9a6700}
        :root{--font:system-ui,-apple-system,"Segoe UI","Apple SD Gothic Neo","Malgun Gothic","Noto Sans KR",sans-serif;--mono:ui-monospace,SFMono-Regular,Menlo,Consolas,"D2Coding",monospace}
        @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--panel:#151b23;--accent:#ff7b6b;--ok:#56d364;--warn:#e3b341}}
        *{box-sizing:border-box}
        body{margin:0 auto;max-width:960px;padding:24px 16px 48px;background:var(--bg);color:var(--fg);font:14px/1.65 var(--font)}
        h1{font-size:1.5rem;margin:0 0 8px}
        h2{font-size:1.2rem;margin:28px 0 10px;padding-bottom:6px;border-bottom:1px solid var(--line)}
        h3{font-size:1.02rem;margin:14px 0 6px}
        h4,h5,h6{font-size:.95rem;margin:12px 0 6px}
        p{margin:6px 0}
        .meta{display:grid;grid-template-columns:max-content 1fr;gap:2px 14px;margin:8px 0 0;color:var(--muted)}
        .meta dt{font-weight:600}
        .meta dd{margin:0}
        .note{margin:10px 0;padding:8px 12px;border:1px solid var(--line);border-left:4px solid var(--warn);border-radius:6px;background:var(--panel)}
        .muted{color:var(--muted)}
        .card{margin:12px 0;padding:10px 14px;border:1px solid var(--line);border-radius:8px}
        .facts{margin:6px 0;padding-left:20px}
        .badge{display:inline-block;margin-left:6px;padding:0 8px;border:1px solid var(--warn);border-radius:10px;color:var(--warn);font-size:.78rem;font-weight:600}
        pre{margin:8px 0;padding:10px 12px;overflow:auto;border:1px solid var(--line);border-radius:6px;background:var(--panel);font:12.5px/1.5 var(--mono);tab-size:4}
        code{font-family:var(--mono);font-size:.92em}
        :not(pre)>code{padding:1px 5px;border-radius:4px;background:var(--panel)}
        blockquote{margin:8px 0;padding:2px 12px;border-left:4px solid var(--line);color:var(--muted)}
        hr{border:0;border-top:1px solid var(--line)}
        .scroll{overflow-x:auto}
        table{border-collapse:collapse;margin:8px 0;min-width:50%}
        th,td{padding:5px 10px;border:1px solid var(--line);text-align:left;vertical-align:top}
        th{background:var(--panel)}
        .num{text-align:right;font-variant-numeric:tabular-nums}
        .mid{text-align:center}
        .md-list{list-style:none;margin:6px 0;padding:0}
        .md-list .mk{display:inline-block;min-width:1.4em;color:var(--muted)}
        .md-list .d1{padding-left:1.4em}.md-list .d2{padding-left:2.8em}.md-list .d3{padding-left:4.2em}.md-list .d4{padding-left:5.6em}.md-list .d5{padding-left:7em}
        footer{margin-top:36px;color:var(--muted);font-size:.85rem;text-align:center}
        @media print{:root{--bg:#fff;--fg:#000;--muted:#444;--line:#999;--panel:#fff}body{max-width:none;padding:0}pre{white-space:pre-wrap;word-break:break-word}.card,pre,table{break-inside:avoid}}
        """;
}
