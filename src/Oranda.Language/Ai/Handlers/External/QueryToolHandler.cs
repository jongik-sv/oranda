using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Language.Ai.Tools;
using Oranda.Language.Errors;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// <c>query</c>(설계 §5.3 「실행」·§6.2, 결정 E-10): SELECT 한 문장을 에이전트 세션에서 실행하고 앞의 행만 돌려준다.
/// <list type="bullet">
/// <item>행 상한 기본 50·최대 200, 시간 상한 기본 30초·최대 60초. 결과 글이 <see cref="MaxRowChars"/> 를 넘을 행부터는 싣지 않고 <c>truncated</c> 를 켠다(게이트 80,000자 한도 안).</item>
/// <item>행 값(<c>row_values</c>)은 수준 QueryRows 에서만 싣는다. 민감 열(<see cref="SensitiveColumns"/>)은 그 수준에서도 <c>***</c>.</item>
/// <item>실행 출처 = <see cref="HistorySource.Agent"/>(이력·정책 규칙 0). 운영 접속은 거부한다.</item>
/// <item>게이트의 문장 검사를 믿되 처리자도 한 번 더 본다: 한 문장·SELECT·잠금 절 없음·DB 링크/금지 호출/사용자 함수 없음.
/// 사용자 함수는 읽기 전용 트랜잭션 없이 돌리지 않으려고 거부한다(이 경로에는 읽기 전용 실행이 없다).</item>
/// </list>
/// </summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class QueryHandler(
    ILanguageService language, IExecutionService execution, IStatementReferenceExtractor extractor, AgentToolboxProvider boxes) : ExternalDbHandlerBase
{
    /// <summary>행 값 글 예산(글자). 열 이름·형식·나머지 칸을 더해도 게이트 한도(80,000자) 안에 들도록 둔다.</summary>
    internal const int MaxRowChars = 60_000;

    public override string ToolName => AppToolNames.Query;

    protected override IReadOnlyList<string> SqlIdsOf(JsonNode result) =>
        result["sql_id"] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0 ? [id] : [];

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var args = inv.Arguments;
        var sql = AppToolArgs.RequiredText(args, AgentFields.Sql);
        var maxRows = AppToolArgs.Number(args, AppFields.MaxRows, AppToolValues.DefaultRows, 1, AppToolValues.MaxRows);
        var timeout = AppToolArgs.Number(args, AppFields.TimeoutSeconds, AppToolValues.DefaultTimeoutSeconds, 1, AppToolValues.MaxTimeoutSeconds);
        var given = Binds(args);

        if (inv.Session.Profile.Environment == ConnectionEnvironment.Production)
        {
            throw new ExternalToolException(AgentErrorCodes.Environment, ExternalToolText.QueryProduction);
        }

        if (!inv.MayExecuteSql)
        {
            throw new ExternalToolException(AgentErrorCodes.Level, ExternalToolText.QueryNotAllowed);
        }

        if (inv.ReadOnlyExecution || inv.Substitutions?.Any(r => r is not null) == true)
        {
            throw new ExternalToolException(AgentErrorCodes.SqlUnsafe, ExternalToolText.QueryUserFunction);
        }

        var statements = language.Split(sql, SplitOptions.Default);
        if (statements.Count != 1 || statements[0] is not { Kind: StatementKind.Query, IsClassificationUncertain: false } stmt)
        {
            throw new ExternalToolException(AgentErrorCodes.SqlNotSingleQuery, ExternalToolText.QuerySingleSelect);
        }

        await CheckStatementAsync(inv, sql, ct).ConfigureAwait(false);

        var missing = stmt.BindNames.Where(n => !given.ContainsKey(n)).ToList();
        if (missing.Count > 0)
        {
            throw new ExternalToolException(AgentErrorCodes.InvalidArguments, ExternalToolText.MissingBinds(missing));
        }

        var binds = stmt.BindNames.ToDictionary(n => n, n => given[n], StringComparer.OrdinalIgnoreCase);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeout));
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await execution.ExecuteAsync(inv.Db, stmt, binds, new ExecutionOptions { Source = HistorySource.Agent }, limit.Token).ConfigureAwait(false);
            JsonObject json;
            await using (result.ResultSet)
            {
                json = result.Status switch
                {
                    ExecutionStatus.Succeeded when result.ResultSet is not null =>
                        await ReadAsync(result.ResultSet, maxRows, inv.Level >= AiAccessLevel.QueryRows, limit.Token).ConfigureAwait(false),
                    ExecutionStatus.Succeeded => throw new ExternalToolException(AgentErrorCodes.SqlNotSingleQuery, ExternalToolText.QuerySingleSelect),
                    ExecutionStatus.Denied => throw new ExternalToolException(AgentErrorCodes.NotAllowed, ExternalToolText.QueryDenied),
                    ExecutionStatus.Cancelled => throw Canceled(limit, ct),
                    _ => throw new ExternalToolException(
                        AgentErrorCodes.ToolFailed, ExternalToolText.QueryFailed(result.Error?.Code),
                        result.Error is { } e ? new InvalidOperationException(e.Code + ": " + e.Message) : null),
                };
            }

            json["elapsed_ms"] = clock.ElapsedMilliseconds;
            var sqlId = await SqlIdAsync(inv, stmt.Text, ct).ConfigureAwait(false);
            if (sqlId is not null)
            {
                json["sql_id"] = sqlId;
            }

            return json;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new ExternalToolException(AgentErrorCodes.Canceled, ExternalToolText.QueryTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ExternalToolException && !ct.IsCancellationRequested)
        {
            if (limit.IsCancellationRequested)
            {
                throw new ExternalToolException(AgentErrorCodes.Canceled, ExternalToolText.QueryTimeout, ex);
            }

            throw new ExternalToolException(AgentErrorCodes.ToolFailed, ExternalToolText.QueryFailed(DbErrorClassification.CodeOf(ex)), ex);
        }
    }

    /// <summary>앞에서부터 <paramref name="maxRows"/> 행(+ 하나 더 있는지)을 읽어 결과 JSON 을 만든다.</summary>
    internal static async Task<JsonObject> ReadAsync(Oranda.Core.Grid.IResultSet set, int maxRows, bool withValues, CancellationToken ct)
    {
        var columns = set.Columns;
        var masked = columns.Select(c => SensitiveColumns.IsSensitive(c.Name)).ToArray();
        var rows = new JsonArray();
        var count = 0;
        var chars = 0;
        var truncated = false;
        for (var page = 0; !truncated; page++)
        {
            var p = await set.GetPageAsync(page, ct).ConfigureAwait(false);
            for (var r = 0; r < p.RowCount; r++)
            {
                if (count == maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new JsonArray();
                for (var c = 0; c < columns.Count; c++)
                {
                    row.Add(masked[c] ? JsonValue.Create(AppToolValues.Masked) : ExternalJson.Cell(p.Columns[c][r]));
                }

                if (withValues)
                {
                    chars += row.ToJsonString().Length + 1;
                    if (chars > MaxRowChars && count > 0)
                    {
                        truncated = true;
                        break;
                    }

                    rows.Add(row);
                }

                count++;
            }

            if (p.IsLast || p.RowCount == 0)
            {
                break;
            }
        }

        var json = new JsonObject
        {
            ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.DbTypeName })]),
        };
        if (withValues)
        {
            json[AgentFields.RowValues] = rows;
        }

        json["row_count"] = count;
        json["truncated"] = truncated;
        return json;
    }

    /// <summary>
    /// 처리자 쪽 문장 검사(게이트 검사의 이중 방어): 한 문장 SELECT, 잠금 절·DB 링크·금지 호출·사용자 함수 없음,
    /// 그리고 현재 스키마와 접속 사용자에서 같은 객체로 풀린다(실행은 접속 사용자 스키마에서 한다).
    /// </summary>
    private async Task CheckStatementAsync(AgentToolInvocation inv, string sql, CancellationToken ct)
    {
        var schema = DefaultSchema(inv);
        var info = await extractor.AnalyzeAsync(inv.Db, sql, schema, ct).ConfigureAwait(false);
        if (info.StatementCount != 1 || info.Kind != StatementKind.Query)
        {
            throw new ExternalToolException(AgentErrorCodes.SqlNotSingleQuery, ExternalToolText.QuerySingleSelect);
        }

        if (info.HasLockingClause || info.HasRemoteReference || info.HasForbiddenCall)
        {
            throw new ExternalToolException(AgentErrorCodes.SqlUnsafe, ExternalToolText.QuerySingleSelect);
        }

        if (info.CallsUserFunction)
        {
            throw new ExternalToolException(AgentErrorCodes.SqlUnsafe, ExternalToolText.QueryUserFunction);
        }

        if (schema is null || string.Equals(schema, inv.Session.Profile.User, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var viaUser = await extractor.AnalyzeAsync(inv.Db, sql, null, ct).ConfigureAwait(false);
        var a = info.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        var b = viaUser.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        if (!a.SequenceEqual(b, StringComparer.Ordinal))
        {
            throw new ExternalToolException(AgentErrorCodes.SqlSchemaUnresolved, ExternalToolText.QuerySchemaMismatch);
        }
    }

    /// <summary>실행한 글의 sql_id: 공유 풀에 이 실행 범위 스키마가 파싱한 커서가 보일 때만(벤치마크와 같은 규칙). V$SQL 을 볼 수 없으면 null.</summary>
    private async Task<string?> SqlIdAsync(AgentToolInvocation inv, string text, CancellationToken ct)
    {
        var computed = SqlIdCalculator.Compute(text);
        var scope = (DefaultSchema(inv) ?? inv.Session.Profile.User).ToUpperInvariant();
        try
        {
            return await boxes.For(inv.Session).TryCursorVisibleAsync(inv.Db, computed, scope, ct).ConfigureAwait(false) is true ? computed : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // 결과는 이미 받았다. sql_id 만 알리지 않는다.
        }
    }

    private static ExternalToolException Canceled(CancellationTokenSource limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ExternalToolException(AgentErrorCodes.Canceled, limit.IsCancellationRequested ? ExternalToolText.QueryTimeout : ExternalToolText.QueryCanceled);
    }

    /// <summary><c>binds</c> 인자(이름 → 값). 이름 앞 <c>:</c> 는 뺀다. 글 = VARCHAR2, 숫자 = NUMBER, 참거짓 = BOOLEAN, null = 빈 VARCHAR2.</summary>
    internal static Dictionary<string, BindValue> Binds(JsonObject args)
    {
        var result = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        if (!args.TryGetPropertyValue(AppFields.Binds, out var node) || node is null)
        {
            return result;
        }

        if (node is not JsonObject binds)
        {
            throw new AppToolArgumentException(AppFields.Binds, "must be an object of name to value");
        }

        foreach (var (rawName, value) in binds)
        {
            var name = rawName.TrimStart(':').Trim();
            if (name.Length == 0)
            {
                throw new AppToolArgumentException(AppFields.Binds, "has an empty name");
            }

            result[name] = value switch
            {
                null => new BindValue(BindDataType.Varchar2, null),
                JsonValue v when v.GetValueKind() == JsonValueKind.String => new BindValue(BindDataType.Varchar2, v.GetValue<string>()),
                JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => new BindValue(BindDataType.Boolean, v.GetValueKind() == JsonValueKind.True),
                JsonValue v when v.GetValueKind() == JsonValueKind.Number
                    && decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => new BindValue(BindDataType.Number, d),
                JsonValue v when v.GetValueKind() == JsonValueKind.Null => new BindValue(BindDataType.Varchar2, null),
                _ => throw new AppToolArgumentException(AppFields.Binds, "values must be strings, numbers, true/false or null"),
            };
        }

        return result;
    }
}
