using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Tuning;

/// <summary>
/// DB 에 쓰는 튜닝 스크립트 문안(TN-16, TSK-06-02). 글만 만들고 실행하지 않는다 — 화면은 새 편집기 탭에 넣기만 한다.
/// 머리 주석에 무엇을 쓰는지·라이선스를 적는다. 이름·값은 문자열 리터럴로만 넣고 작은따옴표를 겹쳐 이스케이프한다.
/// </summary>
public static partial class TuningScripts
{
    /// <summary>
    /// <c>DBMS_STATS.GATHER_TABLE_STATS</c>. <paramref name="column"/> 을 주면 그 열만(히스토그램 SIZE AUTO), 없으면 모든 열·인덱스(cascade).
    /// 대소문자가 섞인 이름은 DBMS_STATS 규칙대로 큰따옴표로 감싸 넘긴다.
    /// </summary>
    public static TuningScript DbmsStats(string owner, string table, string? column = null)
    {
        var o = DictionaryName(owner, nameof(owner));
        var t = DictionaryName(table, nameof(table));
        var methodOpt = column is null
            ? "FOR ALL COLUMNS SIZE AUTO"
            : "FOR COLUMNS SIZE AUTO " + DictionaryName(column, nameof(column));

        var sb = new StringBuilder();
        sb.AppendLine("-- 통계 수집 스크립트(Oranda 가 만든 문안, 실행하지 않았습니다).");
        sb.AppendLine("-- DB 에 씁니다: 딕셔너리 통계를 새로 수집하고, 이 표를 쓰는 커서가 다시 파싱될 수 있습니다.");
        sb.AppendLine("-- 라이선스: 필요 없음(DBMS_STATS).");
        sb.AppendLine("BEGIN");
        sb.AppendLine("  DBMS_STATS.GATHER_TABLE_STATS(");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    ownname          => {Literal(o)},");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    tabname          => {Literal(t)},");
        sb.AppendLine("    estimate_percent => DBMS_STATS.AUTO_SAMPLE_SIZE,");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    method_opt       => {Literal(methodOpt)},");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    cascade          => {(column is null ? "TRUE" : "FALSE")},");
        sb.AppendLine("    no_invalidate    => DBMS_STATS.AUTO_INVALIDATE);");
        sb.AppendLine("END;");
        sb.Append('/');
        return new TuningScript(TuningScriptKind.DbmsStats, sb.ToString());
    }

    /// <summary>
    /// SQL Tuning Advisor: <c>DBMS_SQLTUNE.CREATE_TUNING_TASK</c>·<c>EXECUTE_TUNING_TASK</c>·<c>REPORT_TUNING_TASK</c>.
    /// 태스크 이름을 주지 않으면 <c>ORANDA_&lt;sql_id&gt;</c>.
    /// </summary>
    public static TuningScript TuningAdvisor(string sqlId, string? taskName = null, int timeLimitSeconds = 60)
    {
        var id = CheckSqlId(sqlId);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeLimitSeconds, 1);
        var task = taskName ?? "ORANDA_" + id.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 128)
        {
            throw new ArgumentException("태스크 이름은 1~128자여야 합니다.", nameof(taskName));
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- SQL Tuning Advisor 스크립트(Oranda 가 만든 문안, 실행하지 않았습니다).");
        sb.AppendLine("-- DB 에 씁니다: 튜닝 태스크를 만들고 실행해 결과를 딕셔너리(DBA_ADVISOR_*)에 남깁니다.");
        sb.AppendLine("-- 라이선스: Oracle Tuning Pack 필요(Diagnostics Pack 포함). 보유하지 않았으면 실행하지 마세요.");
        sb.AppendLine("DECLARE");
        sb.AppendLine("  l_task VARCHAR2(128);");
        sb.AppendLine("BEGIN");
        sb.AppendLine("  l_task := DBMS_SQLTUNE.CREATE_TUNING_TASK(");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    sql_id      => {Literal(id)},");
        sb.AppendLine("    scope       => DBMS_SQLTUNE.SCOPE_COMPREHENSIVE,");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    time_limit  => {timeLimitSeconds},");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    task_name   => {Literal(task)},");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    description => {Literal("Oranda: " + id)});");
        sb.AppendLine("  DBMS_SQLTUNE.EXECUTE_TUNING_TASK(task_name => l_task);");
        sb.AppendLine("END;");
        sb.AppendLine("/");
        sb.AppendLine();
        sb.AppendLine("-- 결과 보기(읽기만).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"SELECT DBMS_SQLTUNE.REPORT_TUNING_TASK({Literal(task)}) AS report FROM dual;");
        sb.AppendLine();
        sb.AppendLine("-- 다 본 뒤 태스크 지우기(DB 에 씁니다).");
        sb.Append(CultureInfo.InvariantCulture, $"-- EXEC DBMS_SQLTUNE.DROP_TUNING_TASK({Literal(task)});");
        return new TuningScript(TuningScriptKind.TuningAdvisor, sb.ToString());
    }

    /// <summary>
    /// SQL Plan Baseline 고정: <c>DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE</c>. <paramref name="planHashValue"/> 가 없으면 그 sql_id 의 모든 계획,
    /// <paramref name="fixedPlan"/> 이면 <c>fixed =&gt; 'YES'</c>.
    /// </summary>
    public static TuningScript Baseline(string sqlId, long? planHashValue = null, bool fixedPlan = true)
    {
        var id = CheckSqlId(sqlId);
        if (planHashValue is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planHashValue), "plan_hash_value 는 0 이상이어야 합니다.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- SQL Plan Baseline 고정 스크립트(Oranda 가 만든 문안, 실행하지 않았습니다).");
        sb.AppendLine("-- DB 에 씁니다: 공유 풀의 계획을 SQL Management Base 에 baseline 으로 올립니다.");
        sb.AppendLine("-- 라이선스: Tuning Pack 필요 여부를 확인하세요(SPM 자체는 Enterprise Edition 기능, 튜닝 태스크와 함께 쓰면 Tuning Pack).");
        sb.AppendLine("DECLARE");
        sb.AppendLine("  l_loaded PLS_INTEGER;");
        sb.AppendLine("BEGIN");
        sb.AppendLine("  l_loaded := DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(");
        sb.Append(CultureInfo.InvariantCulture, $"    sql_id          => {Literal(id)}");
        if (planHashValue is { } phv)
        {
            sb.AppendLine(",");
            sb.Append(CultureInfo.InvariantCulture, $"    plan_hash_value => {phv}");
        }

        sb.AppendLine(",");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    fixed           => {Literal(fixedPlan ? "YES" : "NO")});");
        sb.AppendLine("  DBMS_OUTPUT.PUT_LINE('loaded plans: ' || l_loaded);");
        sb.AppendLine("END;");
        sb.Append('/');
        return new TuningScript(TuningScriptKind.Baseline, sb.ToString());
    }

    /// <summary>작은따옴표 문자열 리터럴(<c>'</c> 는 <c>''</c>).</summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// DBMS_STATS 이름 인수 값: 따옴표 없는 일반 이름(대문자·숫자·_$#)은 그대로, 그 밖(소문자·공백·한글)은 <c>"이름"</c>.
    /// 큰따옴표·NUL·줄바꿈이 든 이름은 Oracle 식별자가 될 수 없어 거부한다.
    /// </summary>
    public static string DictionaryName(string name, string paramName = "name")
    {
        CheckIdentifier(name, paramName);
        return PlainName().IsMatch(name) ? name : "\"" + name + "\"";
    }

    private static void CheckIdentifier(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            throw new ArgumentException("이름은 1~128자여야 합니다.", paramName);
        }

        if (name.AsSpan().IndexOfAny("\"\0\r\n") >= 0)
        {
            throw new ArgumentException("이름에 큰따옴표·줄바꿈·NUL 을 쓸 수 없습니다.", paramName);
        }
    }

    private static string CheckSqlId(string sqlId)
    {
        ArgumentNullException.ThrowIfNull(sqlId);
        var id = sqlId.Trim();
        if (!SqlIdPattern().IsMatch(id))
        {
            throw new ArgumentException($"sql_id 형식이 아닙니다(소문자·숫자 13자): {sqlId}", nameof(sqlId));
        }

        return id;
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();

    [GeneratedRegex(@"^[0-9a-z]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdPattern();
}
