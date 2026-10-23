using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>검사할 시나리오 하나(이름과 계산기 값).</summary>
internal sealed record ComboScenarioInput(string Name, IReadOnlyDictionary<string, object?> Values);

/// <summary>조합 검사 요청 하나.</summary>
/// <param name="StatementId">문장 id.</param>
/// <param name="Statement">고친 원문이 반영된 문장 요소.</param>
/// <param name="Fragments">조각 해석기(세션의 <c>Fragments</c>).</param>
/// <param name="Scenarios">함께 검사할 시나리오 값 묶음.</param>
/// <param name="Mode">검사 방식.</param>
/// <param name="Session">실행 계획에 쓸 접속. 없으면 구문 분석만 한다.</param>
/// <param name="FullLimit">이 수 이하의 조건은 모든 조합을 만든다(넘으면 줄인다).</param>
internal sealed record ComboCheckRequest(
    string StatementId,
    MyBatisElement Statement,
    IMyBatisFragmentResolver Fragments,
    IReadOnlyList<ComboScenarioInput> Scenarios,
    ComboCheckMode Mode = ComboCheckMode.Syntax,
    ISession? Session = null,
    int FullLimit = 8);

/// <summary>
/// 조합 검사 실행기(MB-07, TSK-03-04). 조건 켬·끔 조합(<see cref="IMyBatisEvaluator.Combinations"/>)과 시나리오 값으로 문장을 펼치고
/// 펼친 SQL 을 L2 구문 분석(<see cref="ILanguageService.AnalyzeAsync"/>)으로 검사한다. 고르면 실행 계획(<see cref="IExecutionPlanService.ExplainAsync"/>)으로도
/// 확인한다. 실행 계획은 EXPLAIN PLAN 으로 감싸 정책 관문을 거치므로 어떤 경로로도 문장(DML 포함)을 실행하지 않는다.
/// 늘 스레드 풀에서 돌고 취소할 수 있다. 취소되면 그때까지의 결과를 <see cref="ComboCheckReport.Cancelled"/> 로 돌려준다.
/// </summary>
internal sealed partial class ComboCheckRunner(IMyBatisEvaluator evaluator, ILanguageService language, IExecutionPlanService? plans = null)
{
    internal static string EmptySetReason => ComboText.EmptySet;

    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    /// <summary>
    /// 검사한다. <paramref name="progress"/> 는 결과 하나마다 불린다(부른 스레드와 다를 수 있다).
    /// 세션의 조각 해석기는 UI 스레드 상태(문장 목록·탭 원문)를 읽으므로, 부른 스레드에서 include 를 미리 풀어 둔 뒤 스레드 풀로 넘긴다.
    /// </summary>
    public Task<ComboCheckReport> RunAsync(ComboCheckRequest request, IProgress<ComboCheckResult>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fragments = new SnapshotFragments(request.Fragments);
        evaluator.Evaluate(request.Statement, fragments, NoValues, null, new MyBatisEvalOptions(AllOn: true));
        fragments.Freeze();
        var snapshot = request with { Fragments = fragments };
        return Task.Run(() => RunCoreAsync(snapshot, progress, ct), CancellationToken.None);
    }

    private async Task<ComboCheckReport> RunCoreAsync(ComboCheckRequest request, IProgress<ComboCheckResult>? progress, CancellationToken ct)
    {
        var statement = request.Statement;
        var allOn = evaluator.Evaluate(statement, request.Fragments, NoValues, null, new MyBatisEvalOptions(AllOn: true));
        var conditions = Conditions(allOn, statement.Text);
        var combinations = evaluator.Combinations(allOn, request.FullLimit);
        bool reduced = combinations.Any(c => c.Kind != ConditionCombinationKind.Exhaustive);

        var mode = request.Mode;
        string? notice = null;
        if (mode == ComboCheckMode.SyntaxAndPlan && (request.Session is null || plans is null))
        {
            mode = ComboCheckMode.Syntax;
            notice = ComboText.NoSessionNotice;
        }

        var results = new List<ComboCheckResult>(combinations.Count + request.Scenarios.Count);
        bool cancelled = false;
        try
        {
            int number = 0;
            foreach (var combination in combinations)
            {
                ct.ThrowIfCancellationRequested();
                number++;
                var eval = evaluator.Evaluate(statement, request.Fragments, NoValues, combination.Overrides);
                var states = conditions.Select(c => combination.Overrides.TryGetValue(c.Range, out var on) && on).ToList();
                var overrides = combination.Overrides;
                var result = await JudgeAsync(number, ComboText.Combination(number), states, null, eval, statement.Text, mode, request.Session, ct,
                    values => evaluator.Evaluate(statement, request.Fragments, values, overrides)).ConfigureAwait(false);
                results.Add(result);
                progress?.Report(result);
            }

            foreach (var scenario in request.Scenarios)
            {
                ct.ThrowIfCancellationRequested();
                number++;
                var eval = evaluator.Evaluate(statement, request.Fragments, scenario.Values, null);
                var states = conditions.Select(c => eval.Blocks.Any(b => b.Range == c.Range && b.IsOn)).ToList();
                var result = await JudgeAsync(number, scenario.Name, states, scenario.Name, eval, statement.Text, mode, request.Session, ct).ConfigureAwait(false);
                results.Add(result);
                progress?.Report(result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        return new ComboCheckReport(request.StatementId, conditions, results, combinations.Count, reduced, cancelled, mode, notice);
    }

    /// <param name="withValues">조합 행이면 같은 켬·끔으로 값만 바꿔 다시 펼치는 방법(값 없는 <c>${x}</c> 재검사용). 시나리오 행은 null.</param>
    private async Task<ComboCheckResult> JudgeAsync(
        int number, string title, IReadOnlyList<bool> states, string? scenario, EvalResult eval, string statementText, ComboCheckMode mode, ISession? session, CancellationToken ct,
        Func<IReadOnlyDictionary<string, object?>, EvalResult>? withValues = null)
    {
        var sql = eval.Sql;

        // 조각 없음·순환·잘못된 태그는 펼친 SQL 자체를 믿을 수 없다. 계산 못 한 식(Unevaluable)은 조합이 켬·끔을 정했으므로 오류가 아니다.
        // 검사용 가정(Assumed, 값 없는 foreach 를 바인드 자리 하나로)도 펼친 SQL 을 믿을 수 있으므로 오류가 아니다.
        if (eval.Issues.FirstOrDefault(i => !Trusted(i)) is { } issue)
        {
            int line = ComboErrorClassifier.LineOf(statementText, issue.Range.Start);
            return new(number, title, states, scenario, ComboOutcome.Error, ComboText.EvalError,
                ComboText.EvalErrorDetail(issue.Reason, line), sql, null, line);
        }

        // 빈 <set>: MyBatis 가 SET 절을 만들지 않는다. 값이 하나도 없을 때의 일이라 통과로 세고 알린다(조정 결정 A, 2026-10-23).
        if (eval.Blocks.FirstOrDefault(b => b.Tag == "set" && !b.IsOn) is { } emptySet)
        {
            int line = ComboErrorClassifier.LineOf(statementText, emptySet.Range.Start);
            return new(number, title, states, scenario, ComboOutcome.Warning, EmptySetReason,
                ComboText.EmptySetDetail(line), sql, null, line);
        }

        var diagnostics = await language.AnalyzeAsync(sql, ct).ConfigureAwait(false);
        if (diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
        {
            // 값 없는 ${x} 는 빈 글자로 펼쳐진다(ORDER BY ${sort} 등). 조합 행은 자리 글로 채워 다시 펼쳐 보고, 그래도 오류면 치환 탓이 아니므로 오류로 센다.
            // 채우지 못하거나(경로 이름 등) 시나리오 행이면 오류로 세지 않고 알린다.
            if (eval.Substitutions.FirstOrDefault(s => s.Value is null) is { } pending)
            {
                if (withValues is not null)
                {
                    var placeholders = eval.Substitutions.Where(s => s.Value is null)
                        .Select(s => s.Name).Distinct(StringComparer.Ordinal).ToDictionary(n => n, object? (_) => "X", StringComparer.Ordinal);
                    var filled = withValues(placeholders);
                    if (filled.Issues.All(Trusted) && filled.Substitutions.All(s => s.Value is not null))
                    {
                        var again = await language.AnalyzeAsync(filled.Sql, ct).ConfigureAwait(false);
                        if (again.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } real)
                        {
                            var f = ComboErrorClassifier.Classify(filled.Sql, real, filled, statementText);
                            return new(number, title, states, scenario, ComboOutcome.Error, f.Reason, f.Detail, filled.Sql, f.SqlRange, f.SourceLine);
                        }
                    }
                }

                int line = ComboErrorClassifier.LineOf(statementText, pending.Source.Start);
                return new(number, title, states, scenario, ComboOutcome.Warning, ComboText.SubstitutionPending,
                    ComboText.SubstitutionPendingDetail(pending.Name), sql, null, line);
            }

            var e = ComboErrorClassifier.Classify(sql, error, eval, statementText);
            return new(number, title, states, scenario, ComboOutcome.Error, e.Reason, e.Detail, sql, e.SqlRange, e.SourceLine);
        }

        var unsupported = diagnostics.Count > 0;
        if (mode == ComboCheckMode.SyntaxAndPlan && session is not null && plans is not null)
        {
            return await ExplainAsync(number, title, states, scenario, eval, session, ct).ConfigureAwait(false);
        }

        return unsupported
            ? new(number, title, states, scenario, ComboOutcome.Warning, ComboText.Unsupported, diagnostics[0].Message, sql, null, null)
            : new(number, title, states, scenario, ComboOutcome.Pass, ComboText.Pass, string.Empty, sql, null, null);
    }

    // 펼친 SQL 을 믿어도 되는 계산 문제: 조합이 켬·끔을 정한 식, 검사용 가정.
    private static bool Trusted(MyBatisEvalIssue issue) => issue.Kind is MyBatisEvalIssueKind.Unevaluable or MyBatisEvalIssueKind.Assumed;

    private async Task<ComboCheckResult> ExplainAsync(
        int number, string title, IReadOnlyList<bool> states, string? scenario, EvalResult eval, ISession session, CancellationToken ct)
    {
        var sql = eval.Sql;
        // 펼친 SQL 이 문장 여럿으로 나뉘면 첫 문장만 보고 통과로 셀 수 없으므로 대상 아님으로 둔다.
        var split = language.Split(sql, SplitOptions.Default).Where(s => s.Kind != StatementKind.SqlPlus).ToList();
        var parsed = split.Count == 1 ? split[0] : null;
        if (parsed is null || parsed.Kind is StatementKind.PlSql or StatementKind.TransactionControl)
        {
            return new(number, title, states, scenario, ComboOutcome.Warning, ComboText.PlanNotApplicable, ComboText.PlanNotApplicableDetail, sql, null, null);
        }

        if (parsed.BindNames.Count == 0 && eval.Binds.Count > 0)
        {
            parsed = parsed with { BindNames = [.. eval.Binds.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase)] };
        }

        try
        {
            var plan = await plans!.ExplainAsync(session, parsed, ct).ConfigureAwait(false);
            return new(number, title, states, scenario, ComboOutcome.Pass, ComboText.Pass,
                ComboText.PlanChecked(plan.Steps.Count), sql, null, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(number, title, states, scenario, ComboOutcome.Warning, ComboText.PlanDenied, ex.Message, sql, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 접속·권한 탓(읽기 전용 DB·대기 DB, 권한 없음)은 문장 탓이 아니므로 거부로 알린다. 그 밖은 조합 하나의 오류로 남기고 다음 조합을 잇는다.
            var code = OraCode().Match(ex.Message);
            if (code.Success && EnvironmentCodes.Contains(code.Value))
            {
                return new(number, title, states, scenario, ComboOutcome.Warning, ComboText.PlanDenied, ex.Message, sql, null, null);
            }

            return new(number, title, states, scenario, ComboOutcome.Error, code.Success ? $"{ComboText.PlanError} {code.Value}" : ComboText.PlanError,
                ComboText.PlanErrorDetail(ex.Message), sql, null, null);
        }
    }

    private static readonly HashSet<string> EnvironmentCodes = new(StringComparer.Ordinal) { "ORA-01456", "ORA-01031", "ORA-16000" };

    /// <summary>
    /// 미리 푼 조각 해석기. 부른 스레드에서 한 번 펼쳐(include 는 조건과 무관하게 모두 펼친다) 찾은 조각을 기억하고,
    /// <see cref="Freeze"/> 뒤에는 기억한 것만 돌려준다(스레드 풀에서 세션 상태를 읽지 않는다).
    /// </summary>
    private sealed class SnapshotFragments(IMyBatisFragmentResolver inner) : IMyBatisFragmentResolver
    {
        private readonly Dictionary<(string RefId, string? Namespace), MyBatisFragment?> _found = [];
        private volatile bool _frozen;

        public void Freeze() => _frozen = true;

        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            if (_found.TryGetValue((refid, currentNamespace), out var fragment) || _frozen)
            {
                return fragment;
            }

            fragment = inner.Resolve(refid, currentNamespace);
            _found[(refid, currentNamespace)] = fragment;
            return fragment;
        }
    }

    /// <summary>
    /// 검사 전에 표의 열을 그릴 조건 목록(<see cref="ComboCheckReport.Conditions"/> 와 같다)과 검사할 줄 수(조합 + 시나리오, 머리 줄 진행 막대의 끝 값).
    /// UI 스레드에서 부르므로 계산은 한 번만 한다.
    /// </summary>
    public (IReadOnlyList<ComboCondition> Conditions, int Count) Plan(ComboCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allOn = evaluator.Evaluate(request.Statement, request.Fragments, NoValues, null, new MyBatisEvalOptions(AllOn: true));
        return (Conditions(allOn, request.Statement.Text), evaluator.Combinations(allOn, request.FullLimit).Count + request.Scenarios.Count);
    }

    /// <summary>조건(문장 원문의 if·when) 목록. 열 머리는 test 식. foreach 는 조건이 아니라 바인드 자리라 모든 조합에서 항목 하나로 펼친다.</summary>
    internal static IReadOnlyList<ComboCondition> Conditions(EvalResult allOn, string statementText)
    {
        var list = new List<ComboCondition>();
        var seen = new HashSet<TextRange>();
        foreach (var block in allOn.Blocks)
        {
            if (!block.IsCondition || !seen.Add(block.Range))
            {
                continue;
            }

            list.Add(new ComboCondition(block.Range, block.Test ?? block.Tag, ComboErrorClassifier.LineOf(statementText, block.Range.Start)));
        }

        return list;
    }

    /// <summary>
    /// 조합 하나를 시나리오 값으로 바꾼다(「시나리오로 저장」). 끈 조건의 test 식에 쓰인 파라미터는 null(foreach collection 이어도 값 없음 → 바인드 자리 하나),
    /// test 식에 쓰이지 않은 foreach collection 은 항목 하나(꺼진 if 본문 안이면 펼쳐지지 않으니 상관없다),
    /// 그 밖의 파라미터는 자리 값 <c>1</c>(jdbcType 이 숫자면 숫자). 그 값으로 다시 펼쳐 조건 켬·끔이 조합과 같은지 <paramref name="matches"/> 로 알린다
    /// (test 식이 <c>!= null</c> 이 아니면 다를 수 있다. 그때는 사용자가 값을 고친다).
    /// </summary>
    internal Dictionary<string, MyBatisScenarioValue> ScenarioValues(
        MyBatisElement statement, IMyBatisFragmentResolver fragments, IReadOnlyList<ComboCondition> conditions, IReadOnlyList<bool> states, out bool matches)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(states);
        var text = statement.Text;
        var values = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal);
        foreach (var parameter in evaluator.ExtractParameters(statement, fragments))
        {
            bool inOff = false;
            for (int i = 0; i < conditions.Count; i++)
            {
                var head = OpeningTag(text, conditions[i].Range);
                if (!parameter.Locations.Any(l => l.Start >= head.Start && l.End <= head.End))
                {
                    continue;
                }

                inOff |= !states[i];
            }

            bool numeric = parameter.JdbcType is { } jdbc && NumericJdbc.Contains(jdbc);
            var kind = numeric ? MyBatisScenarioValueKind.Number : MyBatisScenarioValueKind.Text;
            values[parameter.Name] = inOff ? MyBatisScenarioValue.Null
                : parameter.Usage.HasFlag(MyBatisParameterUsage.Foreach) ? new MyBatisScenarioValue(MyBatisScenarioValueKind.List, Items: ["1"], ItemKind: kind)
                : new MyBatisScenarioValue(kind, "1");
        }

        var check = new MyBatisScenario { MapperPath = string.Empty, StatementId = string.Empty, Name = string.Empty, Values = values };
        var eval = evaluator.Evaluate(statement, fragments, ToValues(check), null);
        matches = conditions.Select((c, i) => eval.Blocks.Any(b => b.Range == c.Range && b.IsOn) == states[i]).All(x => x);
        return values;
    }

    private static readonly HashSet<string> NumericJdbc = new(StringComparer.OrdinalIgnoreCase)
    {
        "NUMERIC", "DECIMAL", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "DOUBLE", "FLOAT", "REAL",
    };

    // 블록의 여는 태그 범위(test·collection 속성이 든 곳).
    private static TextRange OpeningTag(string text, TextRange block)
    {
        if (block.Start < 0 || block.End > text.Length)
        {
            return block;
        }

        char quote = '\0';
        for (int i = block.Start; i < block.End; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return new TextRange(block.Start, i + 1 - block.Start);
            }
        }

        return block;
    }

    /// <summary>저장한 시나리오 값을 계산기 값으로 바꾼다(숫자는 decimal, 목록은 항목 종류대로).</summary>
    internal static IReadOnlyDictionary<string, object?> ToValues(MyBatisScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in scenario.Values)
        {
            values[name] = value.Kind == MyBatisScenarioValueKind.List
                ? (value.Items ?? []).Select(i => Scalar(value.ItemKind, i)).ToList()
                : Scalar(value.Kind, value.Text);
        }

        return values;
    }

    private static object? Scalar(MyBatisScenarioValueKind kind, string? text) => kind switch
    {
        MyBatisScenarioValueKind.Null => null,
        MyBatisScenarioValueKind.Number when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        MyBatisScenarioValueKind.Boolean => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
        _ => text,
    };

    [GeneratedRegex(@"ORA-\d{5}", RegexOptions.CultureInvariant)]
    private static partial Regex OraCode();
}
