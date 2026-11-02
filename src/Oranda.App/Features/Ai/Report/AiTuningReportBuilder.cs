using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Oranda.App.Features.Ai.Report;

/// <summary>
/// 튜닝 레포트 생성기: 입력(<see cref="AiTuningReportInput"/>) → 글. UI 와 분리한 순수 함수라 같은 입력은 같은 글을 낸다.
/// 절 순서: 제목·메타 → 결론 → 대상 SQL → 재작성 후보 → 인덱스 후보 → 이어 말하기 → 사용한 도구. 내용이 없는 절은 뺀다.
/// </summary>
public static class AiTuningReportBuilder
{
    public const string ConclusionHeading = "결론";
    public const string TargetHeading = "대상 SQL";
    public const string RewritesHeading = "재작성 후보";
    public const string IndexesHeading = "인덱스 후보";
    public const string FollowUpsHeading = "이어 말하기";
    public const string ToolsHeading = "사용한 도구";

    /// <summary>가린 대화 맨 위에 붙는 한 줄.</summary>
    public const string MaskedNote = "리터럴 값을 가린 상태입니다. SQL 의 :v1 같은 표시는 가려서 보낸 값의 자리표시이며 원래 값으로 되돌리지 않았습니다.";

    /// <summary>인덱스 후보 절의 안내 한 줄.</summary>
    public const string IndexNote = "제안만 있습니다. Oranda 는 인덱스를 만들지 않았습니다(실행 안 함).";

    public static string Build(AiTuningReportInput input, AiTuningReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(input);
        return format == AiTuningReportFormat.Markdown ? ToMarkdown(input) : AiTuningReportHtml.Render(input);
    }

    public static string ToMarkdown(AiTuningReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeHeading(input.Title)).Append("\n\n");
        if (input.LiteralsMasked)
        {
            sb.Append("> ").Append(MaskedNote).Append("\n\n");
        }

        foreach (var (name, value) in Meta(input))
        {
            sb.Append("- ").Append(name).Append(": ").Append(value).Append('\n');
        }

        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(input.Result.Summary))
        {
            sb.Append("## ").Append(ConclusionHeading).Append("\n\n").Append(SealFences(input.Result.Summary).TrimEnd('\n')).Append("\n\n");
        }

        if (!string.IsNullOrWhiteSpace(input.TargetSql))
        {
            sb.Append("## ").Append(TargetHeading).Append("\n\n").Append(CodeBlock("sql", input.TargetSql)).Append('\n');
        }

        if (input.Result.Rewrites.Count > 0)
        {
            sb.Append("## ").Append(RewritesHeading).Append("\n\n");
            for (var i = 0; i < input.Result.Rewrites.Count; i++)
            {
                var rewrite = input.Result.Rewrites[i];
                sb.Append("### ").Append(Number(i)).Append(". ").Append(OneLine(rewrite.Label)).Append("\n\n");
                sb.Append("- 근거: ").Append(OneLine(rewrite.Rationale)).Append('\n');
                if (!string.IsNullOrWhiteSpace(rewrite.Benchmark))
                {
                    sb.Append("- 벤치마크: ").Append(OneLine(rewrite.Benchmark)).Append('\n');
                }

                sb.Append('\n').Append(CodeBlock("sql", rewrite.Sql)).Append('\n');
            }
        }

        if (input.Result.IndexSuggestions.Count > 0)
        {
            sb.Append("## ").Append(IndexesHeading).Append("\n\n> ").Append(IndexNote).Append("\n\n");
            for (var i = 0; i < input.Result.IndexSuggestions.Count; i++)
            {
                var index = input.Result.IndexSuggestions[i];
                sb.Append("### ").Append(Number(i)).Append(". ").Append(OneLine(IndexTitle(index))).Append("\n\n");
                sb.Append("- 근거: ").Append(OneLine(index.Rationale)).Append('\n');
                if (!string.IsNullOrWhiteSpace(index.Risk))
                {
                    sb.Append("- 위험: ").Append(OneLine(index.Risk)).Append('\n');
                }

                sb.Append('\n').Append(CodeBlock("sql", index.Ddl)).Append('\n');
            }
        }

        if (input.FollowUps.Count > 0)
        {
            sb.Append("## ").Append(FollowUpsHeading).Append("\n\n");
            for (var i = 0; i < input.FollowUps.Count; i++)
            {
                var followUp = input.FollowUps[i];
                sb.Append("### 질문 ").Append(Number(i)).Append("\n\n");
                foreach (var line in followUp.Question.ReplaceLineEndings("\n").Split('\n'))
                {
                    sb.Append("> ").Append(line).Append('\n');
                }

                sb.Append('\n').Append(string.IsNullOrWhiteSpace(followUp.Answer) ? "(답이 없습니다)" : SealFences(followUp.Answer).TrimEnd('\n')).Append("\n\n");
            }
        }

        if (input.Tools.Count > 0)
        {
            sb.Append("## ").Append(ToolsHeading).Append("\n\n| 도구 | 판단 | 크기 | 걸린 시간 |\n| --- | --- | ---: | ---: |\n");
            foreach (var tool in input.Tools)
            {
                sb.Append("| ").Append(Cell(tool.Name)).Append(" | ").Append(Cell(tool.Verdict)).Append(" | ")
                    .Append(Cell(tool.Size)).Append(" | ").Append(Cell(string.IsNullOrEmpty(tool.Elapsed) ? "-" : tool.Elapsed)).Append(" |\n");
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>제목 아래 메타 줄(이름, 값). 접속 별칭·호스트는 다루지 않는다.</summary>
    internal static IReadOnlyList<(string Name, string Value)> Meta(AiTuningReportInput input)
    {
        List<(string, string)> meta =
        [
            ("생성", input.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("접속 환경", input.EnvironmentText),
        ];
        if (!string.IsNullOrWhiteSpace(input.Model))
        {
            meta.Add(("모델", input.Model.Trim()));
        }

        return meta;
    }

    internal static string IndexTitle(Oranda.Core.Ai.Schemas.TuningIndexSuggestion index) =>
        index.Columns.Count == 0 ? index.Table : $"{index.Table} ({string.Join(", ", index.Columns)})";

    internal static string Number(int zeroBased) => (zeroBased + 1).ToString(CultureInfo.InvariantCulture);

    internal static string OneLine(string? value) => (value ?? string.Empty).ReplaceLineEndings(" ").Trim();

    private static string EscapeHeading(string title) =>
        OneLine(title).Replace("<", "\\<", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);

    private static string Cell(string? value) =>
        OneLine(value).Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>코드 블록. 코드 안의 백틱 줄보다 긴 울타리를 써서 울타리가 깨지지 않게 한다.</summary>
    internal static string CodeBlock(string language, string code)
    {
        var body = (code ?? string.Empty).ReplaceLineEndings("\n").Trim('\n');
        var longest = 0;
        var run = 0;
        foreach (var ch in body)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + language + "\n" + body + "\n" + fence + "\n";
    }

    /// <summary>
    /// AI 가 보낸 마크다운을 그대로 싣되, 닫히지 않은 코드 울타리가 있으면 끝에서 닫아 뒤의 절이 코드로 먹히지 않게 한다.
    /// 줄바꿈은 <c>\n</c> 으로 맞춘다.
    /// </summary>
    internal static string SealFences(string markdown)
    {
        var text = markdown.ReplaceLineEndings("\n");
        var openChar = '\0';
        var openLength = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart(' ');
            if (raw.Length - line.Length > 3 || line.Length < 3 || (line[0] != '`' && line[0] != '~'))
            {
                continue;
            }

            var ch = line[0];
            var length = line.TakeWhile(c => c == ch).Count();
            if (length < 3)
            {
                continue;
            }

            if (openLength == 0)
            {
                openChar = ch;
                openLength = length;
            }
            else if (ch == openChar && length >= openLength && line[length..].Trim().Length == 0)
            {
                openLength = 0;
            }
        }

        return openLength == 0 ? text : text.TrimEnd('\n') + "\n" + new string(openChar, openLength) + "\n";
    }
}
