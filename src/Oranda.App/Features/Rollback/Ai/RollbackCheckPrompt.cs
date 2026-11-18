using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;

namespace Oranda.App.Features.Rollback.Ai;

/// <summary>일지에 남은 변경 한 줄(원복 스크립트가 되돌려야 할 것).</summary>
/// <param name="Seq">실행 차례(1부터).</param>
/// <param name="Verb">「INSERT」·「UPDATE」·「DELETE」·「MERGE」.</param>
/// <param name="TableName">대상 표(<c>OWNER.NAME</c>).</param>
/// <param name="RowsAffected">영향 행 수. 모르면 null.</param>
public sealed record RollbackCheckChange(int Seq, string Verb, string? TableName, long? RowsAffected)
{
    /// <summary>실행한 문장 글. 값이 들어 있다.</summary>
    public string? StatementText { get; init; }
}

/// <summary>대상 표 사이의 FK(자식 → 부모).</summary>
public sealed record RollbackForeignKey(string ChildTable, string ParentTable, string? ConstraintName = null);

/// <summary>표 하나의 부작용 정보(트리거·쓰는 시퀀스).</summary>
public sealed record RollbackTableFacts(string TableName)
{
    public IReadOnlyList<string> Triggers { get; init; } = [];

    public IReadOnlyList<string> Sequences { get; init; } = [];
}

/// <summary>「AI 점검」 입력: 일지의 변경과 원복 스크립트.</summary>
public sealed record RollbackCheckInput(IReadOnlyList<RollbackCheckChange> Changes, string Script)
{
    public IReadOnlyList<RollbackForeignKey> ForeignKeys { get; init; } = [];

    public IReadOnlyList<RollbackTableFacts> Tables { get; init; } = [];
}

/// <summary>점검 띠가 그릴 결과. <see cref="EditorText"/> 는 「고친 스크립트를 편집기에」 단추가 제안으로 넣는 글이다(실행하지 않는다).</summary>
public sealed record RollbackCheckResult(string Title, AiTemplatedAnswer? Answer, string? Conclusion, string? EditorText, string? EditorNote, string RawText, IReadOnlyList<string> Notes);

/// <summary>
/// 원복 스크립트 「AI 점검」(설계 §4.2, 바로 처리 — 원복 창 안 점검 띠). 주 모델(잘못 짚으면 비용이 크다).
/// 일지의 변경과 원복 스크립트를 맞대어 빠진 행·FK 순서·시퀀스·트리거 부작용을 짚고, 고친 스크립트는 제안으로만 준다.
/// <para>
/// 값: 「조회+행 값」 이고 가림 대상이 아니면 스크립트·문장을 그대로 보낸다. 그 밖에는 모든 리터럴을 기존 가림기로 자리표시로 바꾼 글(표·열 이름·문장 수만 남는다)을 보내고
/// 「값 없이 점검했습니다」 를 적는다. 고친 스크립트의 자리표시는 같은 사전으로 되돌린다(같은 입력이면 같은 사전이 나온다). 가릴 수 없으면 보내지 않는다.
/// </para>
/// </summary>
public static partial class RollbackCheckPrompt
{
    public const AiTier Tier = AiCommandTiers.RollbackCheck;

    public const int MaxChanges = 200;

    public const string Title = "원복 스크립트 점검";

    public const string AiOffText = "이 접속은 AI 허용 수준이 꺼져 있어 점검을 보낼 수 없습니다.";

    public const string NothingText = "점검할 원복 스크립트가 없습니다.";

    public const string MaskFailedText = "원복 스크립트의 값을 가릴 수 없어(닫히지 않은 문자열 등) 이 접속에서는 점검을 보낼 수 없습니다.";

    public const string NoValuesNote = "값 없이 점검했습니다.";

    public const string ProposalNote = "고친 스크립트는 제안입니다. 실행 전에 직접 확인하세요.";

    public const string RestoreFailedText = "고친 스크립트에 알 수 없는 자리표시가 있어 원래 값으로 되돌리지 못해 편집기에 넣지 않습니다.";

    public const string FindingsTitle = "점검 결과";

    public const string ConclusionTitle = "결론";

    public const string FixedTitle = "고친 스크립트";

    public static AiAnswerTemplate Template { get; } = new(
    [
        new AiAnswerSection(FindingsTitle, "bullets, each starting with one tag: [빠진 행], [FK 순서], [시퀀스], [트리거] or [기타], then what is wrong in one line; write - 문제 없음 when everything is covered"),
        new AiAnswerSection(ConclusionTitle, "exactly one short line: whether the script is safe to run as it is"),
        new AiAnswerSection(FixedTitle, "the whole corrected script in one ```sql code block, keeping every placeholder unchanged; write 없음 when no change is needed"),
    ]);

    public static AiStripPreparation Prepare(RollbackCheckInput input, AiOneShotRun run, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        var route = run.Route;
        if (route.Context.AccessLevel == AiAccessLevel.Off)
        {
            return AiStripPreparation.Refuse(AiOffText);
        }

        if (string.IsNullOrWhiteSpace(input.Script))
        {
            return AiStripPreparation.Refuse(NothingText);
        }

        var changes = input.Changes.OrderBy(c => c.Seq).Take(MaxChanges).ToList();
        var builder = route.Bundle(Title, masker);
        builder.Add("changes", "일지의 변경", ChangeList(changes, input.Changes.Count), AiAccessLevel.Metadata);
        builder.Add("foreign_keys", "FK", string.Join("\n", input.ForeignKeys.Select(f => $"{OneLine(f.ChildTable)} -> {OneLine(f.ParentTable)}" + (f.ConstraintName is { Length: > 0 } n ? $" ({OneLine(n)})" : string.Empty))), AiAccessLevel.Metadata);
        builder.Add("side_effects", "트리거·시퀀스", SideEffects(input.Tables), AiAccessLevel.Metadata);

        var withValues = RowValues(route);
        if (withValues)
        {
            builder.Add("rollback_script", "원복 스크립트", input.Script, AiAccessLevel.QueryRows, AiContextKind.Sql);
            foreach (var c in changes.Where(c => !string.IsNullOrWhiteSpace(c.StatementText)))
            {
                builder.Add(StatementKey(c), StatementLabel(c), c.StatementText, AiAccessLevel.QueryRows, AiContextKind.Sql);
            }
        }
        else
        {
            if (Shapes(input, changes, masker) is not { } shapes)
            {
                return AiStripPreparation.Refuse(MaskFailedText);
            }

            builder.Add("rollback_script", "원복 스크립트(값 가림)", shapes.Script, AiAccessLevel.Metadata);
            foreach (var (change, text) in shapes.Statements)
            {
                builder.Add(StatementKey(change), StatementLabel(change), text, AiAccessLevel.Metadata);
            }
        }

        var instruction = new StringBuilder();
        instruction.AppendLine("Check this rollback script against the changes recorded in the journal, for the DBA who is about to run it to undo those changes.");
        instruction.AppendLine("Look for: rows the script does not restore (compare verbs, tables and row counts), wrong order because of foreign keys (parents must be restored before children, children deleted before parents), sequence values or identity columns that will not match, and triggers that will fire again or write audit rows.");
        instruction.AppendLine("Each \"statement N\" block is the statement of change N.");
        if (!withValues)
        {
            instruction.AppendLine("Every literal value in the script and the statements was replaced with placeholders such as :v1 because row values may not be sent; judge from tables, columns and statement counts, and never guess the values.");
        }

        instruction.Append("You have no tools and cannot run anything. Never claim you ran the script.");
        return AiStripPreparation.Ready(new AiStripRequest(run, builder.Build(), instruction.ToString(), Template));
    }

    public static RollbackCheckResult Read(string answer, RollbackCheckInput input, AiStripRequest request, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        var parsed = Template.Parse(answer);
        var withValues = RowValues(request.Run.Route);
        var notes = new List<string> { ProposalNote };
        if (!withValues)
        {
            notes.Insert(0, NoValuesNote);
        }

        var conclusion = parsed?[ConclusionTitle]?.Body.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim().TrimStart('-', '*', ' ').Replace("**", string.Empty, StringComparison.Ordinal).Trim()).FirstOrDefault(l => l.Length > 0);
        string? editor = null;
        string? note = null;
        if (parsed?[FixedTitle] is { } fixedPart && Fence().Match(fixedPart.Body.ReplaceLineEndings("\n")) is { Success: true } fence)
        {
            editor = fence.Groups["code"].Value.Trim('\n');
            var vault = request.Bundle.Vault ?? (withValues ? null : Shapes(input, input.Changes.OrderBy(c => c.Seq).Take(MaxChanges).ToList(), masker)?.Vault);
            if (vault is not null)
            {
                if (masker?.Restore(editor, vault) is { Succeeded: true } restored)
                {
                    editor = restored.Text;
                }
                else
                {
                    (editor, note) = (null, RestoreFailedText);
                }
            }
        }

        return new RollbackCheckResult(Title, parsed, conclusion, editor, note, answer, notes);
    }

    /// <summary>행 값을 보내도 되는가(Kit 의 행 값 칸 규칙과 같다: 「조회+행 값」 이고 가림 대상이 아님).</summary>
    private static bool RowValues(AiKitRoute route) =>
        AiAccessPolicy.DataLevel(route.Context.AccessLevel, route.Context.LocalAi) >= AiAccessLevel.QueryRows && !route.MaskRequired;

    /// <summary>값을 모두 가린 스크립트·문장. 같은 입력이면 같은 사전·같은 글이 나온다(보낼 때와 읽을 때). 가리지 못하면 null.</summary>
    private static (LiteralVault Vault, string Script, List<(RollbackCheckChange Change, string Text)> Statements)? Shapes(RollbackCheckInput input, List<RollbackCheckChange> changes, ISqlLiteralMasker? masker)
    {
        if (masker is null)
        {
            return null;
        }

        var withText = changes.Where(c => !string.IsNullOrWhiteSpace(c.StatementText)).ToList();
        var vault = masker.CreateVault(string.Join("\n", withText.Select(c => c.StatementText).Prepend(input.Script)));
        var script = masker.Mask(input.Script, vault);
        if (!script.Succeeded)
        {
            return null;
        }

        var statements = new List<(RollbackCheckChange, string)>();
        foreach (var c in withText)
        {
            var masked = masker.Mask(c.StatementText!, vault);
            if (!masked.Succeeded)
            {
                return null;
            }

            statements.Add((c, masked.Text));
        }

        return (vault, script.Text, statements);
    }

    private static string ChangeList(List<RollbackCheckChange> changes, int total)
    {
        var text = new StringBuilder();
        foreach (var c in changes)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{c.Seq}. {OneLine(c.Verb)} {OneLine(c.TableName)} rows {(c.RowsAffected is { } r ? r.ToString(CultureInfo.InvariantCulture) : "?")}");
        }

        if (total > changes.Count)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"({total - changes.Count} more changes are not shown.)"));
        }

        return text.ToString().TrimEnd();
    }

    private static string SideEffects(IReadOnlyList<RollbackTableFacts> tables)
    {
        var text = new StringBuilder();
        foreach (var t in tables)
        {
            if (t.Triggers.Count > 0)
            {
                text.AppendLine(OneLine(t.TableName) + " triggers: " + string.Join(", ", t.Triggers.Select(OneLine)));
            }

            if (t.Sequences.Count > 0)
            {
                text.AppendLine(OneLine(t.TableName) + " sequences: " + string.Join(", ", t.Sequences.Select(OneLine)));
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string StatementKey(RollbackCheckChange c) => string.Create(CultureInfo.InvariantCulture, $"statement {c.Seq}");

    private static string StatementLabel(RollbackCheckChange c) => string.Create(CultureInfo.InvariantCulture, $"변경 {c.Seq} 문장");

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text.ReplaceLineEndings(" ").Trim();

    [GeneratedRegex(@"```[A-Za-z0-9_+-]*[ \t]*\n(?<code>.*?)\n?```", RegexOptions.Singleline)]
    private static partial Regex Fence();
}
