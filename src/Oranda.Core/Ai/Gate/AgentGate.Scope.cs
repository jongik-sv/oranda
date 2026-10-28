using System.Text.Json.Nodes;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

// 범위 검사(설계 §5.1-3): 범위 = (A) 대상 문장의 참조 객체 ∪ (B) 그 상대(인덱스 표·FK 부모/자식·뷰 기반 표) ∪ (C) 접속의 「보낼 범위」.
public sealed partial class AgentGate
{
    private const string ObjectOutOfScope = "The requested object is not available in the scope of this request.";

    private static readonly string[] ObjectArguments = [AgentFields.ObjectName, AgentFields.Table];

    /// <summary>인자의 객체 이름·sql_id·SQL 글을 범위에 대 본다. 모두 통과하면 null. 객체 이름 인자는 해석된 <c>OWNER.NAME</c> 으로 바꾼다.</summary>
    private async ValueTask<GateOutcome?> CheckScopeAsync(
        AgentSession session, ISession db, AgentToolDefinition tool, CallState state, CancellationToken ct)
    {
        var clean = state.Clean;
        var hasSql = TryGetString(clean, AgentFields.Sql, out var sql);
        var hasSqlId = TryGetString(clean, AgentFields.SqlId, out var sqlId);

        if (tool.Name == AgentToolNames.GetTuningContext
            && !hasSql
            && !hasSqlId
            && string.IsNullOrWhiteSpace(session.Context.StatementText))
        {
            return Deny("no_statement", "There is no statement to work on; pass sql or sql_id, or start from an editor statement.");
        }

        foreach (var argument in ObjectArguments)
        {
            if (!TryGetString(clean, argument, out var name))
            {
                continue;
            }

            // 없는 객체와 범위 밖 객체를 같은 응답으로 돌려 존재 여부를 알려 주지 않는다.
            var resolved = await _extractor.ResolveAsync(db, name, session.Context.CurrentSchema, ct).ConfigureAwait(false);
            if (resolved is null || !await IsInScopeAsync(session, db, resolved, ct).ConfigureAwait(false))
            {
                return Deny("scope", ObjectOutOfScope);
            }

            // 처리자가 원래 글을 다시 풀지 않도록 검사에 쓴 실제 객체 이름으로 바꾼다.
            clean[argument] = JsonValue.Create(resolved.Owner + "." + resolved.Name);
            state.Resolved[argument] = resolved;
        }

        if (hasSqlId && !session.IsKnownSqlId(sqlId))
        {
            return Deny("sql_id_scope", "This sql_id is not known to this request; only the statement of the request and sql_ids returned by tools can be queried.");
        }

        if (hasSql)
        {
            var check = await CheckSqlAsync(session, db, sql, string.Empty, ct).ConfigureAwait(false);
            if (check.Denial is not null)
            {
                return check.Denial;
            }

            // 처리자가 읽는 객체가 검사한 객체와 같도록, 풀어 쓴 글이 있으면 그 글을 넘긴다.
            clean[AgentFields.Sql] = check.Sql;
            state.StatementsChecked = true;
        }
        else if (tool.Name == AgentToolNames.GetTuningContext && !hasSqlId)
        {
            // sql 도 sql_id 도 없으면 처리자는 요청의 대상 문장을 쓴다. 그 글도 sql 인자와 똑같이 검사해 sql 에 채워 넘긴다.
            var check = await CheckSqlAsync(session, db, session.Context.StatementText ?? string.Empty, " (statement of the request)", ct).ConfigureAwait(false);
            if (check.Denial is not null)
            {
                return check.Denial;
            }

            clean[AgentFields.Sql] = check.Sql;
            state.StatementsChecked = true;
        }

        if (clean[AgentFields.Variants] is JsonArray variants)
        {
            foreach (var item in variants)
            {
                if (item is not JsonObject variant
                    || !TryGetString(variant, "label", out var label)
                    || !TryGetString(variant, AgentFields.Sql, out var variantSql))
                {
                    return Deny("invalid_arguments", "Each variant needs a string label and a string sql.");
                }

                var check = await CheckSqlAsync(session, db, variantSql, $" (variant '{Clip(label, 40)}')", ct).ConfigureAwait(false);
                if (check.Denial is not null)
                {
                    return check.Denial;
                }

                variant[AgentFields.Sql] = check.Sql;
            }

            state.StatementsChecked = variants.Count > 0;
        }

        return null;
    }

    /// <summary>
    /// SQL 글 하나: 문장 1개·SELECT·확실한 분류·잠금/DB 링크/사용자 함수 없음·참조 객체 전부 범위 안.
    /// 어긋나면 각각 <c>sql_not_single_query</c>·<c>sql_unsafe</c>·<c>scope</c>.
    /// </summary>
    private async ValueTask<SqlCheck> CheckSqlAsync(AgentSession session, ISession db, string sql, string where, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlCheck.Denied(Deny("sql_not_single_query", $"The SQL{where} must be a single non-empty SELECT statement."));
        }

        var info = await _extractor.AnalyzeAsync(db, sql, session.Context.CurrentSchema, ct).ConfigureAwait(false);
        if (info.StatementCount != 1 || info.Kind != StatementKind.Query || info.IsUncertain)
        {
            return SqlCheck.Denied(Deny("sql_not_single_query", $"The SQL{where} must be a single SELECT statement."));
        }

        if (info.HasLockingClause || info.HasRemoteReference || info.CallsUserFunction)
        {
            return SqlCheck.Denied(Deny("sql_unsafe", $"The SQL{where} must not use FOR UPDATE, database links, user-defined function calls or sequence references."));
        }

        foreach (var reference in info.Objects)
        {
            if (!await IsInScopeAsync(session, db, reference, ct).ConfigureAwait(false))
            {
                return SqlCheck.Denied(Deny("scope", $"The SQL{where} references an object outside the scope of this request."));
            }
        }

        // 추출기가 준 풀어 쓴 글은 분석한 글이 아니다. 처리자에게 넘기기 전에 그 글을 다시 분석해 같은 문장(1개·SELECT·깨끗·같은 참조 객체)인지 확인한다.
        if (!string.IsNullOrWhiteSpace(info.NormalizedSql))
        {
            var again = await _extractor.AnalyzeAsync(db, info.NormalizedSql, session.Context.CurrentSchema, ct).ConfigureAwait(false);
            if (again.StatementCount != 1
                || again.Kind != StatementKind.Query
                || again.IsUncertain
                || again.HasLockingClause
                || again.HasRemoteReference
                || again.CallsUserFunction
                || !SameObjects(info.Objects, again.Objects))
            {
                return SqlCheck.Denied(Deny("sql_unsafe", $"The normalized form of the SQL{where} does not match the analyzed statement."));
            }

            return new SqlCheck(null, info.NormalizedSql);
        }

        // 풀어 쓴 글이 없으면 처리자가 이름을 다시 풀게 된다. 현재 스키마가 접속 사용자와 다르면 검사한 객체와 달라질 수 있어 거부한다.
        var schema = session.Context.CurrentSchema;
        if (!string.IsNullOrWhiteSpace(schema) && !string.Equals(schema, session.Profile.User, StringComparison.OrdinalIgnoreCase))
        {
            return SqlCheck.Denied(Deny("sql_schema_unresolved", "The statement depends on a non-default current schema; unsupported."));
        }

        return new SqlCheck(null, sql);
    }

    private static bool SameObjects(IReadOnlyList<AgentObjectRef> left, IReadOnlyList<AgentObjectRef> right)
    {
        var keys = new HashSet<string>(left.Select(o => o.Key), StringComparer.OrdinalIgnoreCase);
        return keys.SetEquals(right.Select(o => o.Key));
    }

    /// <summary>SQL 글 하나의 검사 결과: 거부(있으면)와 처리자에게 넘길 글.</summary>
    private sealed record SqlCheck(GateOutcome? Denial, string Sql)
    {
        public static SqlCheck Denied(GateOutcome denial) => new(denial, string.Empty);
    }

    private async ValueTask<bool> IsInScopeAsync(AgentSession session, ISession db, AgentObjectRef reference, CancellationToken ct)
    {
        if (session.Send.Matches(reference.Owner, reference.Name))
        {
            return true;
        }

        var statementScope = await GetStatementScopeAsync(session, db, ct).ConfigureAwait(false);
        return statementScope.Contains(reference.Key);
    }

    /// <summary>
    /// (A)+(B): 대상 문장의 참조 객체와 그 상대의 <see cref="AgentObjectRef.Key"/> 집합. 한 실행(<see cref="AgentSession"/>)당 한 번만 계산한다.
    /// 성공한 결과만 남긴다 — 실패하거나 취소되면 다음 호출이 다시 계산하지만, 이번 호출은 다시 시도하지 않고 그 예외로 끝난다
    /// (추출기가 스스로 낸 취소가 무한 재시도가 되지 않게).
    /// </summary>
    private async ValueTask<HashSet<string>> GetStatementScopeAsync(AgentSession session, ISession db, CancellationToken ct)
    {
        var holder = _scopes.GetValue(session, static _ => new ScopeHolder());
        Task<HashSet<string>> pending;
        lock (holder.Gate)
        {
            holder.Pending ??= ComputeStatementScopeAsync(session, db, ct);
            pending = holder.Pending;
        }

        try
        {
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (holder.Gate)
            {
                if (ReferenceEquals(holder.Pending, pending))
                {
                    holder.Pending = null;
                }
            }

            throw;
        }
    }

    private async Task<HashSet<string>> ComputeStatementScopeAsync(AgentSession session, ISession db, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = session.Context.StatementText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return keys;
        }

        var info = await _extractor.AnalyzeAsync(db, text, session.Context.CurrentSchema, ct).ConfigureAwait(false);
        foreach (var reference in info.Objects)
        {
            keys.Add(reference.Key);
        }

        if (info.Objects.Count > 0)
        {
            var related = await _extractor.GetRelatedAsync(db, info.Objects, ct).ConfigureAwait(false);
            foreach (var reference in related)
            {
                keys.Add(reference.Key);
            }
        }

        return keys;
    }

    private sealed class ScopeHolder
    {
        public Lock Gate { get; } = new();

        public Task<HashSet<string>>? Pending { get; set; }
    }
}
