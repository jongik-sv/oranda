using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;

namespace Oranda.App.Features.ChangeJournal.Ai;

/// <summary>일지 한 줄(DDL 보관본·DML 문장). 일지 표의 줄(<c>IJournalRow</c>)이 가진 값을 옮긴다.</summary>
/// <param name="At">실행 시각.</param>
/// <param name="Kind">「DDL」·「INSERT」·「UPDATE」·「DELETE」·「MERGE」 등.</param>
/// <param name="ObjectName">대상 객체(<c>OWNER.NAME</c>).</param>
/// <param name="RowsAffected">영향 행 수. 모르면 null.</param>
public sealed record JournalSummaryEntry(DateTimeOffset At, string Kind, string? ObjectName, long? RowsAffected)
{
    /// <summary>문장 글(값이 들어 있을 수 있다 — 「조회+행 값」 일 때만 보낸다).</summary>
    public string? StatementText { get; init; }

    /// <summary>끝(「커밋」·「롤백」·「열림」). 없으면 null.</summary>
    public string? Outcome { get; init; }
}

/// <summary>「이 기간 요약」 입력.</summary>
public sealed record JournalSummaryInput(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<JournalSummaryEntry> Entries);

/// <summary>요약 띠가 그릴 결과. <see cref="Answer"/> 가 null 이면 <see cref="RawText"/> 를 그대로 보인다.</summary>
public sealed record JournalSummaryResult(string Title, AiTemplatedAnswer? Answer, string RawText, IReadOnlyList<string> Notes);

/// <summary>
/// 변경 일지 「이 기간 요약」(설계 §4.2, 바로 처리 — 일지 위 요약 띠). 가벼운 일.
/// 행 값(문장 글)은 「조회+행 값」 이고 가림 대상이 아닐 때만 보낸다(꾸러미의 행 값 칸, Kit 규칙). 그 밖에는 시각·종류·객체 이름·건수만 보내고 「값 없이 요약했습니다」 를 적는다.
/// </summary>
public static class JournalSummaryPrompt
{
    public const AiTier Tier = AiCommandTiers.JournalSummary;

    /// <summary>줄로 보내는 일지 수 상한(넘으면 합계만).</summary>
    public const int MaxEntries = 200;

    /// <summary>글을 보내는 문장 수 상한.</summary>
    public const int MaxStatements = 50;

    /// <summary>문장 글 하나의 글자 수 상한.</summary>
    public const int MaxStatementChars = 2000;

    public const string Title = "변경 일지 요약";

    public const string AiOffText = "이 접속은 AI 허용 수준이 꺼져 있어 요약을 보낼 수 없습니다.";

    public const string NothingText = "이 기간에 요약할 변경이 없습니다.";

    public const string NoValuesNote = "값 없이 요약했습니다.";

    public const string SummaryTitle = "요약";

    public const string NotableTitle = "눈여겨볼 점";

    public static AiAnswerTemplate Template { get; } = new(
    [
        new AiAnswerSection(SummaryTitle, "one short paragraph: what was changed in this period, on which tables, how much"),
        new AiAnswerSection(NotableTitle, "up to three bullets about risky or unusual changes (large deletes, DDL, rolled back or still open work); write - 없음 when there is nothing"),
    ]);

    public static AiStripPreparation Prepare(JournalSummaryInput input, AiOneShotRun run, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Route.Context.AccessLevel == AiAccessLevel.Off)
        {
            return AiStripPreparation.Refuse(AiOffText);
        }

        if (input.Entries.Count == 0)
        {
            return AiStripPreparation.Refuse(NothingText);
        }

        var entries = input.Entries.OrderBy(e => e.At).ToList();
        var builder = run.Route.Bundle(Title, masker);
        builder.Add("period", "기간", string.Create(CultureInfo.InvariantCulture, $"{input.From:yyyy-MM-dd HH:mm} ~ {input.To:yyyy-MM-dd HH:mm} ({entries.Count} changes)"), AiAccessLevel.Metadata);
        builder.Add("totals", "객체·종류별 합계", Totals(entries), AiAccessLevel.Metadata);
        builder.Add("changes", "변경 목록", Changes(entries), AiAccessLevel.Metadata);
        var n = 0;
        foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e.StatementText)).Take(MaxStatements))
        {
            n++;
            var sql = entry.StatementText!;
            builder.Add(string.Create(CultureInfo.InvariantCulture, $"statement {n}"), string.Create(CultureInfo.InvariantCulture, $"문장 {n}"), sql.Length <= MaxStatementChars ? sql : sql[..MaxStatementChars], AiAccessLevel.QueryRows, AiContextKind.Sql);
        }

        var instruction = new StringBuilder();
        instruction.AppendLine("Summarize the data changes recorded in this change journal for the period, for the DBA who made or must review them.");
        instruction.AppendLine("Each \"statement N\" block, if present, is the text of the N-th change in the list that has one. If no statement blocks are present, the values were not sent: summarize from names and counts only and never guess values.");
        instruction.Append("You have no tools and cannot run anything.");
        return AiStripPreparation.Ready(new AiStripRequest(run, builder.Build(), instruction.ToString(), Template));
    }

    public static JournalSummaryResult Read(string answer, AiStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> notes = request.Bundle.Items.Any(i => i.Key.StartsWith("statement ", StringComparison.Ordinal)) ? [] : [NoValuesNote];
        return new JournalSummaryResult(Title, Template.Parse(answer), answer, notes);
    }

    private static string Totals(List<JournalSummaryEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var g in entries.GroupBy(e => (Object: e.ObjectName ?? "-", e.Kind)).OrderBy(g => g.Key.Object, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
        {
            var rows = g.Where(e => e.RowsAffected is not null).Sum(e => e.RowsAffected!.Value);
            var known = g.Any(e => e.RowsAffected is not null);
            text.Append(CultureInfo.InvariantCulture, $"{OneLine(g.Key.Object)} {OneLine(g.Key.Kind)}: {g.Count()} statements");
            text.AppendLine(known ? string.Create(CultureInfo.InvariantCulture, $", {rows} rows") : string.Empty);
        }

        return text.ToString().TrimEnd();
    }

    private static string Changes(List<JournalSummaryEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var e in entries.Take(MaxEntries))
        {
            text.Append(CultureInfo.InvariantCulture, $"{e.At:yyyy-MM-dd HH:mm:ss} | {OneLine(e.Kind)} | {OneLine(e.ObjectName)} | rows {(e.RowsAffected is { } r ? r.ToString(CultureInfo.InvariantCulture) : "?")}");
            text.AppendLine(e.Outcome is { Length: > 0 } o ? " | " + OneLine(o) : string.Empty);
        }

        if (entries.Count > MaxEntries)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"({entries.Count - MaxEntries} more changes are counted in totals only.)"));
        }

        return text.ToString().TrimEnd();
    }

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text.ReplaceLineEndings(" ").Trim();
}
