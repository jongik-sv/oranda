using System.Globalization;
using System.Text;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>추천 목적(확인 창 라디오).</summary>
public enum AdviceGoal
{
    /// <summary>조회 성능 + 오래된 데이터 정리(기본).</summary>
    QueryAndPurge,

    /// <summary>조회 성능.</summary>
    Query,

    /// <summary>오래된 데이터 정리.</summary>
    Purge,
}

/// <summary>프롬프트 선택.</summary>
/// <param name="SendValues">실제 값 보내기(PA-3). 거짓이면 값은 <see cref="EvidenceValueVault"/> 자리표시자</param>
/// <param name="Language"><c>ko</c> | <c>en</c>(화면 언어, 이유·요약 글 언어)</param>
/// <param name="RequireCandidates">no_partition 뒤 「후보 3개 받기」 — 후보를 꼭 내라고 덧붙임</param>
public sealed record AdvicePromptOptions(AdviceGoal Goal, bool SendValues, string Language, bool RequireCandidates = false);

/// <summary>
/// 근거 → AI 프롬프트(설계 PA-3·PA-7). 목적 → 근거(짧은 표 글) → 출력 규칙 → JSON 스키마 순서.
/// 값을 가리면 상위 값은 열별 <c>V1</c>… 자리표시자, 달 이름은 모은 달 기준 상대 표기(<c>M0</c> = 이번 달, <c>M-1</c> = 지난달)로 바꾼다.
/// 날짜도 보내지 않는다(통계 수집은 「며칠 전」만). 건너뛴 항목의 오류 원문은 넣지 않는다.
/// </summary>
public static class PartitionAdvicePrompt
{
    /// <summary>작은 표 기준(설계 4.1 small-table 과 같음).</summary>
    public const long SmallTableRows = 1_000_000;

    /// <summary>작은 표 블록 기준.</summary>
    public const long SmallTableBlocks = 10_000;

    /// <summary>프롬프트 글과 그 글에 쓴 가림 금고. 금고는 응답을 채울 때 되돌리기에 쓴다.</summary>
    public static (string Prompt, EvidenceValueVault Vault) Build(PartitionEvidence evidence, AdvicePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(options);

        var ko = string.Equals(options.Language?.Trim(), "ko", StringComparison.OrdinalIgnoreCase);
        string L(string k, string e) => ko ? k : e;
        var vault = new EvidenceValueVault();
        var sb = new StringBuilder();

        // 목적
        sb.Append(L("# 목적", "# Goal")).Append('\n');
        var server = string.IsNullOrWhiteSpace(evidence.ServerVersion) ? evidence.Dbms.ToString() : evidence.Dbms + " " + evidence.ServerVersion;
        sb.Append(Inv($"{L("표", "Table")} {evidence.Owner}.{evidence.Table} ({server}). "))
            .Append(L("이 일반 표를 파티션 표로 바꿀 방식을 추천하라.", "Recommend how to convert this non-partitioned table to a partitioned table."))
            .Append('\n');
        sb.Append(L("목적: ", "Goal: ")).Append(options.Goal switch
        {
            AdviceGoal.Query => L("조회 성능(조회 조건에 맞는 키로 파티션 가지치기).", "query performance (partition pruning on the keys used in predicates)."),
            AdviceGoal.Purge => L("오래된 데이터 정리(기간 단위로 DROP·TRUNCATE PARTITION).", "purging old data (DROP or TRUNCATE PARTITION by period)."),
            _ => L("조회 성능 + 오래된 데이터 정리.", "query performance and purging old data."),
        }).Append('\n');
        sb.Append(L("아래 근거는 앱이 모은 숫자다. 근거에 없는 사실은 지어내지 마라.", "The evidence below was collected by the app. Do not invent facts that are not in it.")).Append('\n');

        // 근거
        sb.Append('\n').Append(L("# 근거", "# Evidence")).Append('\n');
        AppendTable(sb, evidence, ko);
        AppendColumns(sb, evidence, ko);
        AppendIndexes(sb, evidence, ko);
        AppendKeys(sb, evidence, ko);
        AppendPredicates(sb, evidence, ko);
        AppendDistributions(sb, evidence, options.SendValues, vault, ko);
        AppendSkipped(sb, evidence, ko);

        // 출력 규칙
        sb.Append('\n').Append(L("# 출력 규칙", "# Output rules")).Append('\n');
        AppendRules(sb, options, ko);

        sb.Append('\n').Append(L("# JSON 스키마", "# JSON schema")).Append('\n');
        sb.Append("```json\n").Append(PartitionAdvice.SchemaJson.Trim()).Append("\n```\n");
        return (sb.ToString(), vault);
    }

    private static void AppendTable(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 표", "## Table")).Append('\n');
        sb.Append(L("행 수 ", "rows ")).Append(Num(e.NumRows, ko))
            .Append(L(" · 블록 ", " · blocks ")).Append(Num(e.Blocks, ko))
            .Append(L(" · 통계 수집 ", " · statistics gathered "));
        if (e.LastAnalyzed is { } analyzed)
        {
            var days = Math.Max(0, (int)Math.Floor((e.CollectedAt - analyzed).TotalDays));
            sb.Append(Inv($"{days}")).Append(L("일 전", " days ago"));
        }
        else
        {
            sb.Append(L("없음", "never"));
        }

        sb.Append('\n');
    }

    private static void AppendColumns(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 열", "## Columns")).Append('\n');
        if (e.Columns.Count == 0)
        {
            sb.Append(None(ko)).Append('\n');
            return;
        }

        sb.Append(L("| 열 | 형식 | NULL 허용 | 고유값 수 | NULL 수 | 히스토그램 | 날짜형(형식 맞는 비율) |", "| column | type | nullable | distinct | nulls | histogram | date-like (match ratio) |")).Append('\n');
        sb.Append("|---|---|---|---|---|---|---|\n");
        foreach (var c in e.Columns)
        {
            var type = c.IsVirtual ? c.DataType + L(" (가상)", " (virtual)") : c.DataType;
            var dateLike = c.DateLike is { } d ? FormatName(d.Format) + " " + Pct(d.MatchRatio) : "-";
            sb.Append(Inv($"| {c.Name} | {type} | {YesNo(c.Nullable)} | {Num(c.NumDistinct, ko)} | {Num(c.NumNulls, ko)} | {c.Histogram ?? "-"} | {dateLike} |")).Append('\n');
        }
    }

    private static void AppendIndexes(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 인덱스", "## Indexes")).Append('\n');
        if (e.Indexes.Count == 0)
        {
            sb.Append(None(ko)).Append('\n');
            return;
        }

        sb.Append(L("| 이름 | 고유 | PK | 열(순서대로) |", "| name | unique | pk | columns (in order) |")).Append('\n');
        sb.Append("|---|---|---|---|\n");
        foreach (var i in e.Indexes)
        {
            sb.Append(Inv($"| {i.Name} | {YesNo(i.Unique)} | {YesNo(i.PrimaryKey)} | {string.Join(", ", i.Columns)} |")).Append('\n');
        }
    }

    private static void AppendKeys(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 제약", "## Constraints")).Append('\n');
        if (e.Keys.Count == 0)
        {
            sb.Append(None(ko)).Append('\n');
            return;
        }

        sb.Append(L("| 이름 | 종류 | 열 |", "| name | kind | columns |")).Append('\n');
        sb.Append("|---|---|---|\n");
        foreach (var k in e.Keys)
        {
            var kind = k.Kind switch
            {
                EvidenceKeyKind.Primary => "PK",
                EvidenceKeyKind.Unique => "UK",
                _ => "FK",
            };
            sb.Append(Inv($"| {k.Name} | {kind} | {string.Join(", ", k.Columns)} |")).Append('\n');
        }
    }

    private static void AppendPredicates(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 실제 조회 조건(V$SQL_PLAN, 실행 수로 가중)", "## Predicates in real queries (V$SQL_PLAN, weighted by executions)")).Append('\n');
        if (e.Predicates.Count == 0)
        {
            sb.Append(None(ko)).Append('\n');
            return;
        }

        sb.Append(L("| 열 | 실행 수 | 문장 수 | 몫 |", "| column | executions | statements | share |")).Append('\n');
        sb.Append("|---|---|---|---|\n");
        foreach (var p in e.Predicates.OrderByDescending(p => p.Share))
        {
            sb.Append(Inv($"| {p.Column} | {Num(p.Executions, ko)} | {Num(p.Statements, ko)} | {Pct(p.Share)} |")).Append('\n');
        }
    }

    private static void AppendDistributions(StringBuilder sb, PartitionEvidence e, bool sendValues, EvidenceValueVault vault, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        sb.Append('\n').Append(L("## 분포", "## Distributions")).Append('\n');
        if (e.Distributions.Count == 0)
        {
            sb.Append(None(ko)).Append('\n');
            return;
        }

        if (!sendValues)
        {
            sb.Append(L(
                "값은 가렸다. V1, V2… 는 열마다 다른 실제 값이고, 달은 M0 = 이번 달, M-1 = 지난달처럼 상대 표기다.",
                "Values are hidden. V1, V2… stand for real values of that column; months are relative: M0 = this month, M-1 = last month.")).Append('\n');
        }

        foreach (var d in e.Distributions)
        {
            var kind = d.Kind == DistributionKind.Monthly ? L("월별", "monthly") : L("상위 값", "top values");
            var sample = d.SamplePercent is { } p ? Inv($"{p:0.###}%") : L("전체", "full scan");
            sb.Append('\n').Append(Inv($"### {d.Column} {kind} ({L("읽은 행", "rows read")} {Num(d.SampledRows, ko)}, {L("표본", "sample")} {sample})")).Append('\n');
            sb.Append(L("| 값 | 건수 |", "| value | count |")).Append('\n');
            sb.Append("|---|---|\n");
            foreach (var b in d.Buckets)
            {
                var label = sendValues ? b.Label : d.Kind == DistributionKind.Monthly ? RelativeMonth(b.Label, e.CollectedAt) ?? vault.Mask(d.Column, b.Label) : vault.Mask(d.Column, b.Label);
                sb.Append(Inv($"| {label} | {Num(b.Count, ko)} |")).Append('\n');
            }

            if (d.RestCount > 0)
            {
                var rest = d.Kind == DistributionKind.Monthly ? L("(36개월보다 이른 달)", "(months before the last 36)") : L("(그 밖의 값)", "(other values)");
                sb.Append(Inv($"| {rest} | {Num(d.RestCount, ko)} |")).Append('\n');
            }

            if (d.NullCount is > 0)
            {
                sb.Append(Inv($"| NULL | {Num(d.NullCount, ko)} |")).Append('\n');
            }
        }
    }

    private static void AppendSkipped(StringBuilder sb, PartitionEvidence e, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        if (e.Skipped.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(L("## 모으지 못한 근거", "## Evidence not collected")).Append('\n');
        foreach (var s in e.Skipped)
        {
            var item = s.Item switch
            {
                EvidenceItem.Columns => L("열·형식", "columns"),
                EvidenceItem.Keys => L("키·인덱스", "keys and indexes"),
                EvidenceItem.Statistics => L("통계", "statistics"),
                EvidenceItem.Predicates => L("조회 조건", "predicates"),
                _ => L("분포", "distributions"),
            };
            var reason = s.Reason switch
            {
                EvidenceSkipReason.Off => L("사용자가 끔", "turned off by the user"),
                EvidenceSkipReason.NoPrivilege => L("권한 없음", "no privilege"),
                EvidenceSkipReason.NotSupported => L("지원 안 함", "not supported"),
                EvidenceSkipReason.NoStatistics => L("통계 없음", "no statistics"),
                _ => L("오류", "error"),
            };
            sb.Append("- ").Append(item);
            if (s.Column is { Length: > 0 } column)
            {
                sb.Append(" (").Append(column).Append(')');
            }

            sb.Append(": ").Append(reason).Append('\n');
        }
    }

    private static void AppendRules(StringBuilder sb, AdvicePromptOptions o, bool ko)
    {
        string L(string k, string en) => ko ? k : en;
        var rows = Inv($"{SmallTableRows:N0}");
        var blocks = Inv($"{SmallTableBlocks:N0}");
        var rules = new List<string>
        {
            L("아래 JSON 스키마에 맞는 JSON 객체 하나만 답하라(설명 글·코드 울타리 없이).", "Answer with exactly one JSON object that matches the JSON schema below (no prose, no code fence)."),
            L("후보는 1~3개, 좋은 순서(rank 1 = 가장 좋음).", "Give 1 to 3 candidates, best first (rank 1 = best)."),
            L("reasons 의 이유마다 근거 숫자를 꼭 인용하라(예: 「ORDER_DT 조건 몫 72.0%」).", "Every reason must cite at least one evidence number (for example \"ORDER_DT predicate share 72.0%\")."),
            L(
                $"행 수가 {rows} 미만이거나 블록이 {blocks} 미만이면 verdict = no_partition, candidates = [] 로 답하라.",
                $"If the table has fewer than {rows} rows or fewer than {blocks} blocks, answer verdict = no_partition with candidates = []."),
            L("key_columns 는 위 열 목록의 이름만, local_indexes 는 위 인덱스 목록의 이름만 쓴다.", "key_columns must be names from the column list; local_indexes must be names from the index list."),
            L("문자열·숫자 날짜형 키는 INTERVAL 을 쓸 수 없다: interval = none.", "String or number date-like keys cannot use INTERVAL: interval = none."),
            L("PK·UK 에 키 열이 없으면 그 인덱스는 GLOBAL 로 남는다 — cautions 에 적어라.", "If a PK or UK does not contain the key columns, its index stays GLOBAL — mention it in cautions."),
            L("키로 보였지만 고르지 않은 열은 not_chosen 에 이유와 함께 적는다.", "List columns that look like keys but were not chosen in not_chosen, with the reason."),
        };
        if (!o.SendValues)
        {
            rules.Add(L(
                "가린 값(V1, V2…)은 groups.values 에 그대로 쓴다. 값을 짐작해 지어내지 마라. layout.start 는 비워 둔다(앱이 분포로 정함).",
                "Write hidden values (V1, V2…) as they are in groups.values. Do not guess real values. Leave layout.start empty (the app picks it from the distribution)."));
        }

        if (o.RequireCandidates)
        {
            rules.Add(L(
                "이번에는 후보를 반드시 1~3개 내라. 이득이 작다고 보면 verdict = partition 으로 두고 그 이유를 cautions 에 적는다.",
                "This time you must give 1 to 3 candidates. If you think the gain is small, keep verdict = partition and say why in cautions."));
        }

        rules.Add(L("title·summary·reasons·cautions·not_chosen 의 글은 한국어로 쓴다.", "Write title, summary, reasons, cautions and not_chosen reasons in English."));
        foreach (var rule in rules)
        {
            sb.Append("- ").Append(rule).Append('\n');
        }
    }

    // 'YYYY-MM' → M0·M-1…(모은 달 기준). 꼴이 다르면 null(금고로 가림).
    private static string? RelativeMonth(string label, DateTime collectedAt)
    {
        if (!DateTime.TryParseExact(label.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return null;
        }

        var diff = (month.Year - collectedAt.Year) * 12 + (month.Month - collectedAt.Month);
        return diff switch
        {
            0 => "M0",
            > 0 => Inv($"M+{diff}"),
            _ => Inv($"M{diff}"),
        };
    }

    private static string FormatName(DateLikeFormat format) => format switch
    {
        DateLikeFormat.Yyyymmdd => "YYYYMMDD",
        DateLikeFormat.Yyyymm => "YYYYMM",
        _ => "YYYY-MM-DD",
    };

    private static string Num(long? value, bool ko) => value is { } v ? Inv($"{v:N0}") : ko ? "모름" : "unknown";

    private static string Pct(double ratio) => Inv($"{ratio * 100:0.0}%");

    private static string YesNo(bool value) => value ? "Y" : "N";

    private static string None(bool ko) => ko ? "(없음)" : "(none)";

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
