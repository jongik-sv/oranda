using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;

namespace Oranda.App.Features.Flashback.Ai;

/// <summary>버전 하나에서 앞 버전과 달라진 칸(<c>VersionsDiff</c> 가 찾은 칸).</summary>
public sealed record FlashbackVersionChange(string Column, string? OldValue, string? NewValue);

/// <summary>VERSIONS 결과의 버전 한 줄. 시간 순으로 넣는다.</summary>
/// <param name="Operation">VERSIONS_OPERATION(I·U·D). 모르면 빈 글.</param>
/// <param name="StartTime">VERSIONS_STARTTIME 글. 없으면 null.</param>
/// <param name="StartScn">VERSIONS_STARTSCN 글. 없으면 null.</param>
/// <param name="Changes">앞 버전과 달라진 칸.</param>
public sealed record FlashbackVersion(string Operation, string? StartTime, string? StartScn, IReadOnlyList<FlashbackVersionChange> Changes)
{
    /// <summary>키 값 글(「CUST_ID=501」). 행 값이라 「조회+행 값」 일 때만 보낸다. 같은 글이면 같은 행으로 묶는다.</summary>
    public string? KeyText { get; init; }
}

/// <summary>「차이 요약」 입력.</summary>
public sealed record FlashbackDiffInput(string TableName, IReadOnlyList<string> KeyColumns, IReadOnlyList<FlashbackVersion> Versions);

/// <summary>요약 띠가 그릴 결과.</summary>
public sealed record FlashbackDiffSummaryResult(string Title, AiTemplatedAnswer? Answer, string RawText, IReadOnlyList<string> Notes);

/// <summary>
/// 플래시백 버전 「차이 요약」(설계 §4.2, 바로 처리 — 차이 화면 위 한 단락). 가벼운 일.
/// 바뀐 열 이름·작업·시각·행 번호(키 값 대신 1부터)는 늘 보내고, 값(앞뒤 값·키 값)은 꾸러미의 행 값 칸이라 「조회+행 값」 이고 가림 대상이 아닐 때만 나간다.
/// 민감 열(<see cref="SensitiveColumns"/>)의 값은 그때도 「[가림]」 이다.
/// </summary>
public static class FlashbackDiffSummaryPrompt
{
    public const AiTier Tier = AiCommandTiers.FlashbackDiffSummary;

    public const int MaxVersions = 200;

    public const int MaxValueChars = 200;

    public const string Title = "플래시백 차이 요약";

    public const string AiOffText = "이 접속은 AI 허용 수준이 꺼져 있어 차이 요약을 보낼 수 없습니다.";

    public const string NothingText = "요약할 버전이 없습니다.";

    public const string NoValuesNote = "값 없이 요약했습니다.";

    public const string SummaryTitle = "요약";

    public const string NotableTitle = "눈여겨볼 점";

    public static AiAnswerTemplate Template { get; } = new(
    [
        new AiAnswerSection(SummaryTitle, "one short paragraph: how the rows changed over time (inserted, updated which columns, deleted)"),
        new AiAnswerSection(NotableTitle, "up to three bullets about changes worth checking (a deleted row, a value changed back and forth, many updates in a short time); write - 없음 when there is nothing"),
    ]);

    public static AiStripPreparation Prepare(FlashbackDiffInput input, AiOneShotRun run)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Route.Context.AccessLevel == AiAccessLevel.Off)
        {
            return AiStripPreparation.Refuse(AiOffText);
        }

        if (input.Versions.Count == 0)
        {
            return AiStripPreparation.Refuse(NothingText);
        }

        var versions = input.Versions.Take(MaxVersions).ToList();
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        int RowOf(FlashbackVersion v)
        {
            var key = v.KeyText ?? string.Empty;
            if (!rows.TryGetValue(key, out var n))
            {
                n = rows[key] = rows.Count + 1;
            }

            return n;
        }

        var shape = new StringBuilder();
        var values = new StringBuilder();
        for (var i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            var row = RowOf(v);
            shape.Append(CultureInfo.InvariantCulture, $"#{i + 1} row {row} | operation {OneLine(v.Operation)} | time {OneLine(v.StartTime)} | scn {OneLine(v.StartScn)}");
            shape.AppendLine(v.Changes.Count == 0 ? string.Empty : " | changed: " + string.Join(", ", v.Changes.Select(c => OneLine(c.Column))));
            values.Append(CultureInfo.InvariantCulture, $"#{i + 1} row {row} key {OneLine(Hidden(input.KeyColumns, v.KeyText))}");
            values.AppendLine(v.Changes.Count == 0 ? string.Empty : " | " + string.Join("; ", v.Changes.Select(Value)));
        }

        if (input.Versions.Count > versions.Count)
        {
            shape.AppendLine(string.Create(CultureInfo.InvariantCulture, $"({input.Versions.Count - versions.Count} later versions are not shown.)"));
        }

        var builder = run.Route.Bundle(Title, masker: null);
        builder.Add("table", "테이블", OneLine(input.TableName) + " (key columns: " + (input.KeyColumns.Count == 0 ? "-" : string.Join(", ", input.KeyColumns.Select(OneLine))) + ")", AiAccessLevel.Metadata);
        builder.Add("versions", "버전 목록(값 없음)", shape.ToString().TrimEnd(), AiAccessLevel.Metadata);
        builder.Add("values", "바뀐 값", values.ToString().TrimEnd(), AiAccessLevel.QueryRows);

        var instruction = "Summarize how the rows of this table changed between the flashback versions, for the DBA reviewing them. "
            + "\"row N\" numbers the distinct rows (by key) in order of appearance. If the values block is missing, the values were not sent: summarize from column names and operations only and never guess values. "
            + "You have no tools and cannot run anything.";
        return AiStripPreparation.Ready(new AiStripRequest(run, builder.Build(), instruction, Template));
    }

    public static FlashbackDiffSummaryResult Read(string answer, AiStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> notes = request.Bundle.Items.Any(i => i.Key == "values") ? [] : [NoValuesNote];
        return new FlashbackDiffSummaryResult(Title, Template.Parse(answer), answer, notes);
    }

    private static string Value(FlashbackVersionChange c) =>
        SensitiveColumns.IsSensitive(c.Column)
            ? OneLine(c.Column) + ": " + ISqlLiteralMasker.RedactedPlaceholder
            : OneLine(c.Column) + ": " + Cut(c.OldValue) + " -> " + Cut(c.NewValue);

    /// <summary>키 열 중 민감 열이 있으면 키 글 전체를 가린다.</summary>
    private static string? Hidden(IReadOnlyList<string> keys, string? keyText) =>
        keys.Any(SensitiveColumns.IsSensitive) ? ISqlLiteralMasker.RedactedPlaceholder : Cut(keyText);

    private static string Cut(string? value) =>
        value is null ? "NULL" : value.Length <= MaxValueChars ? OneLine(value) : OneLine(value[..MaxValueChars]) + "…";

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text.ReplaceLineEndings(" ").Trim();
}
