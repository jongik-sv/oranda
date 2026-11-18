using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.PlsqlEditor.Ai;

/// <summary>
/// PL/SQL 오류 고치기 입력. 편집기가 가진 값(<c>CompileErrorsViewModel</c> 의 <see cref="CompileError"/>·편집기 소스)을 그대로 옮긴다.
/// </summary>
/// <param name="ObjectName">객체 이름(<c>OWNER.NAME</c>).</param>
/// <param name="ObjectType">유형(<c>PROCEDURE</c>·<c>PACKAGE BODY</c> 등).</param>
/// <param name="Source">편집기의 소스 전체(줄 번호 = 오류 줄 번호 기준).</param>
/// <param name="Errors">컴파일 오류(<c>ALL_ERRORS</c>). 경고는 보내지 않는다.</param>
public sealed record PlsqlFixInput(string ObjectName, string ObjectType, string Source, IReadOnlyList<CompileError> Errors)
{
    /// <summary>패키지 본문이면 명세 전체(참고용, 고치지 않는다). 없으면 null.</summary>
    public string? SpecSource { get; init; }
}

/// <summary>보낸 소스 조각(1부터, 끝 줄 포함).</summary>
public readonly record struct PlsqlSourcePartRange(int Start, int End);

/// <summary>띠·제안 막대가 그릴 결과. <see cref="EditorText"/> 가 있으면 제안 막대로 넣고, 없으면 <see cref="EditorNote"/> 를 한 줄로 보인다.</summary>
/// <param name="Title">띠 제목.</param>
/// <param name="Answer">까닭·고친 소스 칸. 틀이 깨졌으면 null.</param>
/// <param name="Reason">까닭 한 줄(제안 막대에 적는다). 없으면 null.</param>
/// <param name="EditorText">고친 조각을 끼워 넣은 소스 전체(원래 값으로 되돌린 글). 만들지 못했으면 null.</param>
/// <param name="EditorNote">편집기 글을 만들지 못한 까닭. 만들었으면 null.</param>
/// <param name="RawText">받은 글 그대로.</param>
/// <param name="Notes">띠 아래 안내.</param>
public sealed record PlsqlFixResult(string Title, AiTemplatedAnswer? Answer, string? Reason, string? EditorText, string? EditorNote, string RawText, IReadOnlyList<string> Notes);

/// <summary>
/// PL/SQL 편집기 「오류 고치기」(설계 §3.3, 바로 처리 — 편집기 제안 막대): 입력 → 띠 요청, 답 글 → 고친 소스 전체 + 까닭 한 줄.
/// <para>
/// SQL 「오류 고치기」(<c>FixErrorCommand</c>)와 같은 규칙을 따른다: 가리는 대화는 서버 오류 글을 보내지 않고 오류 번호와 줄만, 가릴 수 없는 소스는 보내지 않는다,
/// 고침은 가장 작게, 주석은 그대로. 다른 점: 소스가 크면(<see cref="WholeSourceMaxLines"/> 줄 넘음) 오류 주변(앞뒤 <see cref="ContextLines"/> 줄)만 조각으로 보내고,
/// 답은 조각 단위로 받아 원래 소스에 끼워 넣는다(조각 범위가 보낸 것과 다르면 넣지 않는다). 도구를 쓰지 않으므로 허용 수준은 꺼짐만 아니면 된다(소스 = 메타데이터).
/// </para>
/// </summary>
public static partial class PlsqlFixPrompt
{
    /// <summary>요청 등급.</summary>
    public const AiTier Tier = AiCommandTiers.PlsqlFixError;

    /// <summary>오류 줄 앞뒤로 보내는 줄 수(설계 §3.3 「오류 줄 앞뒤 40줄」).</summary>
    public const int ContextLines = 40;

    /// <summary>이 줄 수 이하면 소스 전체를 한 조각으로 보낸다.</summary>
    public const int WholeSourceMaxLines = 200;

    /// <summary>한 번에 보내는 오류 수 상한(앞쪽부터).</summary>
    public const int MaxErrors = 20;

    public const string Title = "PL/SQL 오류 고치기";

    public const string AiOffText = "이 접속은 AI 허용 수준이 꺼져 있어 오류 고치기를 보낼 수 없습니다.";

    public const string NoErrorText = "고칠 컴파일 오류가 없습니다. 먼저 컴파일해 오류를 확인하세요.";

    public const string MaskFailedText = "이 소스는 리터럴 값을 가릴 수 없어(닫히지 않은 문자열 등) 가리는 접속에서는 오류를 고칠 수 없습니다. 소스를 직접 고친 뒤 다시 시도하세요.";

    public const string MessageWithheldNote = "오류 메시지 본문은 보내지 않고 오류 번호와 줄만 보냈습니다.";

    public const string PartialNote = "소스가 커서 오류 주변만 보냈습니다.";

    public const string NoFixText = "AI 답에 고친 소스가 없어 편집기에 넣지 않습니다.";

    public const string RangeMismatchText = "AI 가 돌려준 범위가 보낸 소스 조각과 맞지 않아 편집기에 넣지 않습니다.";

    public const string RestoreFailedText = "AI 답에 알 수 없는 자리표시가 있어 원래 값으로 되돌리지 못해 편집기에 넣지 않습니다.";

    public const string ReasonTitle = "까닭";

    public const string FixedTitle = "고친 소스";

    /// <summary>답 틀: 까닭 한 줄 → 고친 조각.</summary>
    public static AiAnswerTemplate Template { get; } = new(
    [
        new AiAnswerSection(ReasonTitle, "exactly one short line saying what was wrong and what you changed; it is shown on the editor's suggestion bar"),
        new AiAnswerSection(FixedTitle, "only the fixed_part blocks described above, no other text and no code fences"),
    ]);

    /// <summary>보낼 소스 조각. 작은 소스는 전체 한 조각, 큰 소스는 오류마다 앞뒤 <see cref="ContextLines"/> 줄을 잘라 겹치거나 붙은 조각을 합친다.</summary>
    public static IReadOnlyList<PlsqlSourcePartRange> Parts(string source, IReadOnlyList<CompileError> errors)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(errors);
        var count = Lines(source).Length;
        if (count <= WholeSourceMaxLines)
        {
            return [new PlsqlSourcePartRange(1, count)];
        }

        var parts = new List<PlsqlSourcePartRange>();
        foreach (var line in Errors(errors).Select(e => Math.Clamp(e.Line, 1, count)).Order())
        {
            var start = Math.Max(1, line - ContextLines);
            var end = Math.Min(count, line + ContextLines);
            if (parts.Count > 0 && start <= parts[^1].End + 1)
            {
                parts[^1] = parts[^1] with { End = Math.Max(parts[^1].End, end) };
            }
            else
            {
                parts.Add(new PlsqlSourcePartRange(start, end));
            }
        }

        return parts;
    }

    /// <summary>띠 요청을 준비한다.</summary>
    public static AiStripPreparation Prepare(PlsqlFixInput input, AiOneShotRun run, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        var route = run.Route;
        if (route.Context.AccessLevel == AiAccessLevel.Off)
        {
            return AiStripPreparation.Refuse(AiOffText);
        }

        var errors = Errors(input.Errors);
        if (errors.Count == 0)
        {
            return AiStripPreparation.Refuse(NoErrorText);
        }

        var lines = Lines(input.Source);
        var parts = Parts(input.Source, errors);
        var builder = route.Bundle(Title, masker);
        builder.Add("compile_errors", "컴파일 오류", ErrorText(errors, route.MaskRequired, lines), AiAccessLevel.Metadata);
        foreach (var part in parts)
        {
            builder.Add(PartKey(part), PartLabel(part), string.Join("\n", lines[(part.Start - 1)..part.End]), AiAccessLevel.Metadata, AiContextKind.Sql);
        }

        builder.Add("package_spec", "패키지 명세(참고)", input.SpecSource, AiAccessLevel.Metadata, AiContextKind.Sql);
        var bundle = builder.Build();
        if (bundle.Omitted.Any(o => o.Reason is AiContextOmitReason.MaskFailed or AiContextOmitReason.MaskUnavailable))
        {
            return AiStripPreparation.Refuse(MaskFailedText);
        }

        var whole = parts is [{ Start: 1 } only] && only.End == lines.Length;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Fix the compile errors of this Oracle PL/SQL {OneLine(input.ObjectType)} {OneLine(input.ObjectName)}.");
        text.AppendLine("You have no tools and cannot compile or run anything; suggest only, a person reviews and compiles.");
        text.AppendLine(whole
            ? string.Create(CultureInfo.InvariantCulture, $"The whole source ({lines.Length} lines) is in the block named \"source_part 1-{lines.Length}\".")
            : string.Create(CultureInfo.InvariantCulture, $"The source has {lines.Length} lines; only the parts around the errors are sent. Each block named \"source_part S-E\" holds lines S to E (1-based) of the whole source. Do not invent the lines you cannot see."));
        text.AppendLine("Line numbers in compile_errors are 1-based in the whole source.");
        if (route.MaskRequired)
        {
            text.AppendLine("The error message text is withheld because literal values are masked in this conversation; only the error codes and lines are given.");
        }

        if (input.SpecSource is { Length: > 0 })
        {
            text.AppendLine("package_spec is for reference only; do not change it.");
        }

        text.AppendLine("Make the smallest change that removes the errors. Keep every comment, the indentation and the casing of unchanged code.");
        text.Append("For each part you change, write the complete new text of that part between <fixed_part start=\"S\" end=\"E\"> and </fixed_part>, using exactly the S and E of the source_part it replaces; leave out parts you do not change. The new text may have more or fewer lines than the original part.");
        return AiStripPreparation.Ready(new AiStripRequest(run, bundle, text.ToString(), Template));
    }

    /// <summary>답을 읽어 까닭 한 줄과, 고친 조각을 끼워 넣은 소스 전체(편집기 제안)를 만든다. 가린 대화면 <paramref name="masker"/> 로 원래 값을 되돌린다.</summary>
    public static PlsqlFixResult Read(string answer, PlsqlFixInput input, AiStripRequest request, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        var parsed = Template.Parse(answer);
        var reason = parsed?[ReasonTitle] is { } r ? FirstLine(r.Body) : null;
        var parts = Parts(input.Source, Errors(input.Errors));
        var notes = new List<string>();
        if (request.Bundle.Masked)
        {
            notes.Add(MessageWithheldNote);
        }

        if (parts is not [{ Start: 1 }] || parts[0].End != Lines(input.Source).Length)
        {
            notes.Add(PartialNote);
        }

        var (editor, note) = Splice(answer, input.Source, parts, request.Bundle.Vault, masker);
        return new PlsqlFixResult(Title, parsed, reason, editor, note, answer, notes);
    }

    private static (string? Text, string? Note) Splice(string answer, string source, IReadOnlyList<PlsqlSourcePartRange> parts, LiteralVault? vault, ISqlLiteralMasker? masker)
    {
        var fixes = FixedPart().Matches(answer.ReplaceLineEndings("\n"))
            .Select(m => (Range: new PlsqlSourcePartRange(int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture)), Text: m.Groups["text"].Value))
            .ToList();
        if (fixes.Count == 0)
        {
            return (null, NoFixText);
        }

        if (fixes.Any(f => !parts.Contains(f.Range)) || fixes.Select(f => f.Range).Distinct().Count() != fixes.Count)
        {
            return (null, RangeMismatchText);
        }

        var lines = Lines(source).ToList();
        foreach (var fix in fixes.OrderByDescending(f => f.Range.Start))
        {
            var text = fix.Text;
            if (vault is not null)
            {
                if (masker?.Restore(text, vault) is not { Succeeded: true } restored)
                {
                    return (null, RestoreFailedText);
                }

                text = restored.Text;
            }

            lines.RemoveRange(fix.Range.Start - 1, fix.Range.End - fix.Range.Start + 1);
            lines.InsertRange(fix.Range.Start - 1, text.ReplaceLineEndings("\n").Split('\n'));
        }

        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return (string.Join(newline, lines), null);
    }

    /// <summary>오류 목록 글. 가리는 대화는 번호와 줄만, 아니면 줄·열·서버 글과 그 줄의 글까지.</summary>
    private static string ErrorText(List<CompileError> errors, bool masked, string[] lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < errors.Count; i++)
        {
            var e = errors[i];
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}. line {e.Line}");
            if (masked)
            {
                text.Append(": ").AppendLine(Code(e));
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $", column {e.Column}: ").AppendLine(OneLine(e.Message));
            if (e.Line >= 1 && e.Line <= lines.Length)
            {
                text.Append("   the line reads: ").AppendLine(lines[e.Line - 1].Trim());
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>오류 번호(「PLS-00201」). 글 머리에 없으면 <see cref="CompileError.MessageNumber"/> 로 만든다.</summary>
    private static string Code(CompileError error)
    {
        var match = CodeShape().Match(error.Message ?? string.Empty);
        if (match.Success)
        {
            return match.Value.Trim();
        }

        return error.MessageNumber > 0 ? string.Create(CultureInfo.InvariantCulture, $"PLS-{error.MessageNumber:00000}") : "(error code unknown)";
    }

    private static List<CompileError> Errors(IReadOnlyList<CompileError> errors) => errors.Where(e => !e.IsWarning).Take(MaxErrors).ToList();

    private static string PartKey(PlsqlSourcePartRange part) => string.Create(CultureInfo.InvariantCulture, $"source_part {part.Start}-{part.End}");

    private static string PartLabel(PlsqlSourcePartRange part) => string.Create(CultureInfo.InvariantCulture, $"소스 {part.Start}~{part.End} 줄");

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text.ReplaceLineEndings(" ").Trim();

    private static string? FirstLine(string body) =>
        body.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim().TrimStart('-', '*', '>', ' ').Replace("**", string.Empty, StringComparison.Ordinal).Trim()).FirstOrDefault(l => l.Length > 0);

    private static string[] Lines(string source) => source.ReplaceLineEndings("\n").Split('\n');

    [GeneratedRegex(@"^\s*[A-Z]{2,4}-\d{4,5}")]
    private static partial Regex CodeShape();

    [GeneratedRegex(@"<fixed_part\s+start=""(?<s>\d+)""\s+end=""(?<e>\d+)""\s*>\n?(?<text>.*?)\n?</fixed_part>", RegexOptions.Singleline)]
    private static partial Regex FixedPart();
}
