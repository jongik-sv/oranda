using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// 밖의 에이전트가 자동으로 실행하는 SELECT 의 문장 검사(계획 agent-cli-phase2 §3.1, 결정 E-25). <c>query</c>·<c>run_tab</c>·<c>export_result</c> 가 함께 쓴다.
/// 게이트는 App 도구(탭 실행·내보내기)의 SQL 을 보지 못하므로 처리자가 이 검사를 직접 부른다. 정책 규칙 0(출처 Agent = Query 만)과 이중이다.
/// <list type="bullet">
/// <item>한 문장, 실효 분류 Query(SELECT/WITH), 분류 불확실 거부 → <c>sql_not_single_query</c>.</item>
/// <item>잠금 절(<c>FOR UPDATE</c>)·DB 링크·금지 호출(시퀀스·SYS 패키지·URI 형) → <c>sql_unsafe</c>.</item>
/// <item>사용자 함수: <see cref="ExternalSelectOptions.VerifyUserFunctions"/> 가 거짓이면 모두 거부, 참이면 <see cref="IUserFunctionSafetyChecker"/> 가 안전하다고 한 것만.</item>
/// <item>이름 풀기: 검사하기 바로 전에 그 세션의 현재 스키마(<c>SYS_CONTEXT('USERENV','CURRENT_SCHEMA')</c>)를 읽어 그 스키마로 푼다 —
/// 실행이 쓰는 이름 풀이와 같다(<c>ALTER SESSION SET CURRENT_SCHEMA</c>·PL/SQL 블록의 <c>EXECUTE IMMEDIATE</c>·로그온 트리거 모두 반영).
/// 읽지 못하면 거부한다(닫힌 쪽) → <c>sql_schema_unresolved</c>.</item>
/// <item>호출자가 이름을 미리 푼 스키마(<see cref="ExternalSelectOptions.DefaultSchema"/>)가 세션의 현재 스키마와 다르면 두 쪽에서 같은 객체로 풀려야 한다 → <c>sql_schema_unresolved</c>.</item>
/// </list>
/// 운영 허용·수준 판정은 여기서 하지 않는다(허가 정책 함수의 일).
/// </summary>
public interface IExternalSelectGuard
{
    /// <summary><paramref name="sql"/> 을 <paramref name="db"/> 기준으로 검사한다. 거부도 예외가 아니라 결과로 돌려준다(취소·DB 오류는 예외).</summary>
    ValueTask<ExternalSelectCheck> CheckAsync(ISession db, string sql, ExternalSelectOptions options, CancellationToken ct);
}

/// <summary>검사 조건.</summary>
public sealed record ExternalSelectOptions
{
    /// <summary>
    /// 호출자(게이트)가 이름을 미리 푼 스키마(<c>query</c> = 실행 범위의 현재 스키마, 없으면 접속 사용자).
    /// 검사는 늘 세션에서 읽은 현재 스키마로 풀고, 이 값이 있고 그것과 다르면 두 쪽에서 같은 객체로 풀리는지 본다.
    /// null 이면 그 비교를 하지 않는다(<c>run_tab</c>·<c>export_result</c>: 미리 푼 이름이 없다).
    /// </summary>
    public string? DefaultSchema { get; init; }

    /// <summary>참 = 사용자 함수가 있으면 확인기로 확인해 안전한 것만 허용. 거짓 = 사용자 함수는 모두 거부(<c>query</c>).</summary>
    public bool VerifyUserFunctions { get; init; }

    /// <summary>참 = 참조를 하나라도 실제 객체로 풀지 못했으면(분류 불확실) 거부. 게이트가 이미 본 <c>query</c> 는 거짓.</summary>
    public bool RequireResolvedNames { get; init; }
}

/// <summary>거부 까닭(도구마다 글을 고르게).</summary>
public enum ExternalSelectRefusal
{
    None,

    /// <summary>한 문장 SELECT/WITH 가 아니다(또는 분류 불확실).</summary>
    NotSingleQuery,

    /// <summary>참조 이름을 실제 객체로 풀지 못했다.</summary>
    Unresolved,

    /// <summary>잠금 절·DB 링크·금지 호출.</summary>
    Unsafe,

    /// <summary>확인 못 한(또는 허용하지 않는) 사용자 함수.</summary>
    UserFunction,

    /// <summary>현재 스키마와 실행 스키마에서 다른 객체로 풀린다.</summary>
    SchemaMismatch,

    /// <summary>세션의 현재 스키마를 읽지 못했다(이름을 어디서 풀지 몰라 닫힌 쪽으로 거부).</summary>
    SchemaUnknown,
}

/// <summary>검사 결과. 통과면 <see cref="Statement"/> 가 그 한 문장(바인드 이름 포함).</summary>
public sealed record ExternalSelectCheck(ExternalSelectRefusal Refusal, string? Code, string? Message, SqlStatement? Statement)
{
    public bool Allowed => Refusal == ExternalSelectRefusal.None;

    internal static ExternalSelectCheck Ok(SqlStatement statement) => new(ExternalSelectRefusal.None, null, null, statement);

    internal static ExternalSelectCheck Refused(ExternalSelectRefusal refusal) => refusal switch
    {
        ExternalSelectRefusal.NotSingleQuery => new(refusal, AgentErrorCodes.SqlNotSingleQuery, ExternalToolText.QuerySingleSelect, null),
        ExternalSelectRefusal.Unresolved => new(refusal, AgentErrorCodes.SqlNotSingleQuery, ExternalSelectGuard.UnresolvedText, null),
        ExternalSelectRefusal.Unsafe => new(refusal, AgentErrorCodes.SqlUnsafe, ExternalToolText.QuerySingleSelect, null),
        ExternalSelectRefusal.UserFunction => new(refusal, AgentErrorCodes.SqlUnsafe, ExternalToolText.QueryUserFunction, null),
        ExternalSelectRefusal.SchemaMismatch => new(refusal, AgentErrorCodes.SqlSchemaUnresolved, ExternalToolText.QuerySchemaMismatch, null),
        ExternalSelectRefusal.SchemaUnknown => new(refusal, AgentErrorCodes.SqlSchemaUnresolved, ExternalSelectGuard.SchemaUnknownText, null),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}

/// <inheritdoc cref="IExternalSelectGuard"/>
[OrandaService(typeof(IExternalSelectGuard))]
internal sealed class ExternalSelectGuard(
    ILanguageService language, IStatementReferenceExtractor extractor, IUserFunctionSafetyChecker? userFunctions, IExecutionService execution) : IExternalSelectGuard
{
    /// <summary>참조를 풀지 못했을 때 글(밖의 에이전트가 읽는다, 영어).</summary>
    internal const string UnresolvedText = "Some names in the statement do not resolve to tables or views visible to this connection. Check the names or write OWNER.NAME.";

    /// <summary>세션의 현재 스키마를 읽지 못했을 때 글(밖의 에이전트가 읽는다, 영어). DB 오류 원문은 싣지 않는다.</summary>
    internal const string SchemaUnknownText = "Could not read the current schema of this session, so names in the statement cannot be checked. Try again or write OWNER.NAME.";

    /// <summary>세션의 현재 스키마를 읽는 문장. Oracle·Tibero 둘 다 된다(Tibero 실측: docs/design/tibero-catalog-map.md).</summary>
    internal const string CurrentSchemaSql = "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL";

    /// <summary>
    /// 게이트가 넘긴 사용자 함수 판정(<see cref="AgentToolInvocation.ReadOnlyExecution"/>·<see cref="AgentToolInvocation.Substitutions"/>)이 실행을 막는가.
    /// <c>query</c> 경로에는 읽기 전용 실행이 없으므로 게이트가 읽기 전용·대체를 요구한 문장은 실행하지 않는다.
    /// </summary>
    internal static bool GateFlagsUserFunction(AgentToolInvocation inv) =>
        inv.ReadOnlyExecution || inv.Substitutions?.Any(r => r is not null) == true;

    public async ValueTask<ExternalSelectCheck> CheckAsync(ISession db, string sql, ExternalSelectOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.NotSingleQuery);
        }

        var statements = language.Split(sql, SplitOptions.Default);
        if (statements.Count != 1 || statements[0] is not { Kind: StatementKind.Query, IsClassificationUncertain: false } stmt)
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.NotSingleQuery);
        }

        // 실행과 같은 이름 풀이: 그 세션의 지금 현재 스키마. 세션이 따라가며 기억한 값은 PL/SQL 블록·로그온 트리거가 바꾼 것을 놓친다.
        if (await CurrentSchemaAsync(db, ct).ConfigureAwait(false) is not { } schema)
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.SchemaUnknown);
        }

        var info = await extractor.AnalyzeAsync(db, sql, schema, ct).ConfigureAwait(false);
        if (info.StatementCount != 1 || info.Kind != StatementKind.Query)
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.NotSingleQuery);
        }

        if (info.HasLockingClause || info.HasRemoteReference || info.HasForbiddenCall)
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.Unsafe);
        }

        if (info.CallsUserFunction && !await UserFunctionsAllowedAsync(db, info, schema, options, ct).ConfigureAwait(false))
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.UserFunction);
        }

        if (options.RequireResolvedNames && info.IsUncertain)
        {
            return ExternalSelectCheck.Refused(ExternalSelectRefusal.Unresolved);
        }

        if (string.IsNullOrWhiteSpace(options.DefaultSchema) || string.Equals(schema, options.DefaultSchema, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSelectCheck.Ok(stmt);
        }

        var viaCaller = await extractor.AnalyzeAsync(db, sql, options.DefaultSchema, ct).ConfigureAwait(false);
        var a = info.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        var b = viaCaller.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        return a.SequenceEqual(b, StringComparer.Ordinal) ? ExternalSelectCheck.Ok(stmt) : ExternalSelectCheck.Refused(ExternalSelectRefusal.SchemaMismatch);
    }

    /// <summary>
    /// <paramref name="db"/> 의 지금 현재 스키마(<see cref="CurrentSchemaSql"/>, 에이전트 출처·이력 없음). 실패·빈 값이면 null(호출자가 거부한다). 취소는 예외로 둔다.
    /// </summary>
    private async ValueTask<string?> CurrentSchemaAsync(ISession db, CancellationToken ct)
    {
        try
        {
            var stmt = new SqlStatement(CurrentSchemaSql, default, StatementKind.Query) { Verb = "SELECT" };
            var options = new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true };
            var result = await execution.ExecuteAsync(db, stmt, new Dictionary<string, BindValue>(), options, ct).ConfigureAwait(false);
            if (result.ResultSet is not { } set)
            {
                return null;
            }

            await using (set.ConfigureAwait(false))
            {
                if (result.Status != ExecutionStatus.Succeeded)
                {
                    return null;
                }

                // 드라이버가 글 칸을 string 이 아닌 형식으로 줄 수도 있어 사용자 함수 확인기와 같이 글로 바꿔 읽는다(DBNull = 빈 글).
                var page = await set.GetPageAsync(0, ct).ConfigureAwait(false);
                var name = page.RowCount > 0 && page.Columns.Count > 0 ? Convert.ToString(page.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture) : null;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // 닫힌 쪽: 어디서 풀지 모르면 통과시키지 않는다
        }
    }

    /// <summary>
    /// 사용자 함수 허용: 확인 모드에서 분류된 후보가 있고(분류 못 한 호출은 게이트와 같이 거부) 확인기가 <see cref="UserFunctionVerdict.Safe"/> 라 할 때만.
    /// 안전 판정 = 닿는 PL/SQL 객체에 자치 트랜잭션·외부 접근이 없다. SELECT 안에서 부른 함수는 자치 트랜잭션 없이 DML·COMMIT 을 하지 못한다(ORA-14551·14552).
    /// </summary>
    private async ValueTask<bool> UserFunctionsAllowedAsync(ISession db, AgentStatementInfo info, string? schema, ExternalSelectOptions options, CancellationToken ct)
    {
        if (!options.VerifyUserFunctions || userFunctions is null || info.UserFunctions is not { Count: > 0 } functions)
        {
            return false;
        }

        return await userFunctions.CheckAsync(db, functions, schema, ct).ConfigureAwait(false) == UserFunctionVerdict.Safe;
    }
}
