using System.Globalization;
using Oranda.App.Features.Localization;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>
/// AI 파티션 추천 화면 문구. 앱 확인(<see cref="AdviceCheck.TextKey"/>)·건너뛴 항목·진행 단계 글은 strings-ko/en.json 의
/// <c>TableEdit.partition.ai.*</c> 키로 찾는다(Core 는 code 만 낸다).
/// </summary>
public static class PartitionAdvisorText
{
    /// <summary>건너뛴 이유 키 머리.</summary>
    public const string SkipKeyPrefix = "TableEdit.partition.ai.skip.";

    /// <summary>근거 항목 이름 키 머리.</summary>
    public const string ItemKeyPrefix = "TableEdit.partition.ai.item.";

    /// <summary>진행 단계 키 머리.</summary>
    public const string StepKeyPrefix = "TableEdit.partition.ai.step.";

    /// <summary>수준 표시(✔ / ⚠ / ✖).</summary>
    public static string Mark(AdviceCheckLevel level) => level switch
    {
        AdviceCheckLevel.Ok => "✔",
        AdviceCheckLevel.Warn => "⚠",
        _ => "✖",
    };

    /// <summary>
    /// 앱 확인 한 줄. 문구에 <c>{0}</c>… 가 있으면 <see cref="AdviceCheck.Args"/> 를 넣고, 없으면 값을 괄호로 덧붙인다.
    /// 걸린 열·인덱스(<see cref="AdviceCheck.Subject"/>)는 앞에 둔다. 모르는 code 는 code 그대로.
    /// </summary>
    public static string Check(AdviceCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var args = check.Args ?? [];
        var template = check is { Code: AdviceCheckCodes.Dialect, Level: AdviceCheckLevel.Warn } && args is ["rebuild", ..]
            ? TableEditText.T("이 서버는 한 문장으로 바꾸지 못해 다시 만들기 스크립트로 만듭니다", "This server cannot convert in one statement, so a rebuild script is made")
            : UiText.Get(check.TextKey, check.Code);
        var body = template.Contains("{0}", StringComparison.Ordinal) ? FillArgs(template, args) : template + ArgsSuffix(check.Code, args);
        return string.IsNullOrEmpty(check.Subject) ? body : check.Subject + ": " + body;
    }

    /// <summary>
    /// code 마다 정한 Args 뜻(<see cref="AdviceCheckCodes"/> 주석)대로 덧붙이는 글(「 — NULL 154,000건」). Args 가 없거나 비면 빈 글.
    /// 모르는 code 는 값을 괄호로.
    /// </summary>
    internal static string ArgsSuffix(string code, IReadOnlyList<string> args)
    {
        string A(int i) => i < args.Count ? args[i].Trim() : string.Empty;
        string N(int i) => long.TryParse(A(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.InvariantCulture) : A(i);
        if (args.Count == 0 || args.All(string.IsNullOrWhiteSpace))
        {
            return string.Empty;
        }

        var text = code switch
        {
            AdviceCheckCodes.KeyType => TableEditText.T($"형식 {A(0)}", $"type {A(0)}"),
            AdviceCheckCodes.IntervalString => A(0),
            AdviceCheckCodes.IntervalNull or AdviceCheckCodes.RangeNull => TableEditText.T($"NULL {N(0)}건", $"{N(0)} NULL rows"),
            AdviceCheckCodes.DateLikeRatio => args.Count > 1
                ? TableEditText.T($"맞는 비율 {A(0)}% · 표본 {N(1)}행", $"{A(0)}% match · {N(1)} sampled rows")
                : TableEditText.T($"맞는 비율 {A(0)}%", $"{A(0)}% match"),
            AdviceCheckCodes.PkGlobal => TableEditText.T($"제약 열 {A(0)}", $"constraint columns {A(0)}"),
            AdviceCheckCodes.LocalUnique => TableEditText.T($"빠진 키 열 {A(0)}", $"missing key columns {A(0)}"),
            AdviceCheckCodes.HashPow2 => TableEditText.T($"{A(0)}개", $"{A(0)} partitions"),
            AdviceCheckCodes.ListCover => TableEditText.T($"안 덮인 값 {N(0)}개 · {N(1)}건", $"{N(0)} values · {N(1)} rows not covered"),
            AdviceCheckCodes.BoundsOrder => A(0),
            AdviceCheckCodes.Dialect => args.Count > 1 ? UiText.Get(A(1), A(1)) : A(0),
            AdviceCheckCodes.SmallTable => A(1).Length > 0
                ? TableEditText.T($"행 {N(0)} · 블록 {N(1)}", $"{N(0)} rows · {N(1)} blocks")
                : TableEditText.T($"행 {N(0)}", $"{N(0)} rows"),
            AdviceCheckCodes.StatsOld => TableEditText.T($"{A(0)}일 전", $"{A(0)} days ago"),
            AdviceCheckCodes.MethodUnknown => A(0),
            _ => string.Join(", ", args.Where(a => !string.IsNullOrWhiteSpace(a))),
        };
        return text.Length == 0 ? string.Empty : " (" + text + ")";
    }

    /// <summary>건너뛴 항목 한 줄(「분포 · PLANT_CD — 권한 없음」).</summary>
    public static string Skip(EvidenceSkip skip)
    {
        ArgumentNullException.ThrowIfNull(skip);
        var item = UiText.Get(ItemKeyPrefix + skip.Item, skip.Item.ToString());
        var reason = UiText.Get(SkipKeyPrefix + skip.Reason, skip.Reason.ToString());
        var head = string.IsNullOrEmpty(skip.Column) ? item : item + " · " + skip.Column;
        return string.IsNullOrWhiteSpace(skip.Detail) ? $"{head} — {reason}" : $"{head} — {reason} ({OneLine(skip.Detail)})";
    }

    /// <summary>진행 단계 이름.</summary>
    public static string Step(EvidenceStep step) => UiText.Get(StepKeyPrefix + step, step.ToString());

    /// <summary>근거 항목 이름.</summary>
    public static string Item(EvidenceItem item) => UiText.Get(ItemKeyPrefix + item, item.ToString());

    private static string FillArgs(string template, IReadOnlyList<string> args)
    {
        // 자리 문구(번역에서 자리를 쓸 때): 값이 모자라면 빈 글로 채운다(채우기 Problems 는 Args 가 없을 수 있다).
        var values = new object[Math.Max(args.Count, 4)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < args.Count ? args[i] : string.Empty;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, values);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
