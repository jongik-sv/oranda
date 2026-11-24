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

    // 생성자 모양은 그대로 둔다(시험·IT 가 직접 만든다). query 는 사용자 함수를 확인하지 않으므로 확인기 없이 만든다.
    // 가드는 실행 엔진으로 에이전트 세션의 현재 스키마를 검사 직전에 읽는다.
    private readonly ExternalSelectGuard _guard = new(language, extractor, userFunctions: null, execution);

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

        if (!AgentGrantPolicy.ExecutesIn(inv.Session.Profile.Environment))
        {
            throw new ExternalToolException(AgentErrorCodes.Environment, ExternalToolText.QueryProduction);
        }

        if (!inv.MayExecuteSql)
        {
            throw new ExternalToolException(AgentErrorCodes.Level, ExternalToolText.QueryNotAllowed);
        }

        if (ExternalSelectGuard.GateFlagsUserFunction(inv))
        {
            throw new ExternalToolException(AgentErrorCodes.SqlUnsafe, ExternalToolText.QueryUserFunction);
        }

        var stmt = await CheckStatementAsync(inv, sql, ct).ConfigureAwait(false);

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
                        await ReadAsync(result.ResultSet, maxRows, inv.Level >= AiAccessLevel.QueryRows, limit.Token, maskSensitive: !inv.Session.Context.LocalAi, narrow: !Oranda.Core.Ai.Access.AiAccessPolicy.MasksSensitiveColumns(inv.Session.Profile)).ConfigureAwait(false),
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

    /// <summary>가져온 행 수 = 결과의 <c>row_count</c>(행 값을 싣지 않는 수준에서도 센다).</summary>
    protected override long? RowCountOf(JsonNode result) =>
        result["row_count"] is JsonValue v && v.TryGetValue<int>(out var rows) ? rows : null;

    /// <summary>
    /// 앞에서부터 <paramref name="maxRows"/> 행(+ 하나 더 있는지)을 읽어 결과 JSON 을 만든다. <paramref name="maskSensitive"/> 면 민감 열을 가리고,
    /// <paramref name="narrow"/>(접속의 민감 가림 끔, D-069)면 가림을 「항상 가리는 열」(비밀번호·주민번호·카드·계좌·주소·인증값)로 좁힌다.
    /// </summary>
    internal static async Task<JsonObject> ReadAsync(Oranda.Core.Grid.IResultSet set, int maxRows, bool withValues, CancellationToken ct, bool maskSensitive = true, bool narrow = false)
    {
        var columns = set.Columns;
        var masked = columns.Select(c => maskSensitive && SensitiveColumns.Hides(c.Name, narrow)).ToArray();
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
    /// 처리자 쪽 문장 검사(게이트 검사의 이중 방어, <see cref="IExternalSelectGuard"/>): 한 문장 SELECT, 잠금 절·DB 링크·금지 호출·사용자 함수 없음,
    /// 그리고 게이트가 이름을 푼 스키마(실행 범위의 현재 스키마, 없으면 접속 사용자)와 에이전트 세션의 지금 현재 스키마(가드가 읽는다)에서 같은 객체로 풀린다.
    /// 사용자 함수는 확인 없이 모두 거부한다.
    /// </summary>
    private async Task<SqlStatement> CheckStatementAsync(AgentToolInvocation inv, string sql, CancellationToken ct)
    {
        var options = new ExternalSelectOptions { DefaultSchema = DefaultSchema(inv) ?? inv.Session.Profile.User };
        var check = await _guard.CheckAsync(inv.Db, sql, options, ct).ConfigureAwait(false);
        return check.Statement ?? throw new ExternalToolException(check.Code!, check.Message!);
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
