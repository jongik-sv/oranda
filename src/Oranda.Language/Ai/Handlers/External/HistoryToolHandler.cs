using System.Globalization;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.History;
using Oranda.Core.Storage;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// <c>get_history</c>(설계 §5.3 「결과·오류·이력」, Host Lang): 실행 이력을 최근 것부터 준다. DB 를 부르지 않는다(<see cref="IHistoryStore"/>).
/// <list type="bullet">
/// <item>허가 안 접속 중 수준 Metadata 이상인 접속의 이력만 준다. <c>connection</c> 을 주면 그 접속만.</item>
/// <item>저장소가 이미 비밀을 가렸다. 운영 접속 이력은 리터럴·주석도 가린다(<see cref="ISqlLiteralMasker.MaskPredicate"/>, 읽지 못하면 글 전체를 가림).</item>
/// <item>SQL 글은 <c>sql_text</c> 칸(게이트의 리터럴 가림 칸 이름)에 넣고 한 항목 <see cref="MaxSqlChars"/> 자에서 자른다(잘리면 <c>sql_truncated</c>).</item>
/// </list>
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class GetHistoryHandler(IHistoryStore history, ISqlLiteralMasker masker) : IAppToolHandler
{
    internal const int MaxSqlChars = 2000;

    private const int DefaultLimit = 20;

    private const int MaxLimit = 50;

    /// <summary>출처로 거를 때 저장소에서 한 번에 읽을 행(출처 조건이 저장소 질의에 없다).</summary>
    private const int SourceScan = 500;

    public string ToolName => AppToolNames.GetHistory;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
        AppToolOutputs.Guard(async () =>
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var args = invocation.Arguments;
            var limit = AppToolArgs.Number(args, AppFields.Limit, DefaultLimit, 1, MaxLimit);
            HistorySource? source = null;
            if (AppToolArgs.Text(args, AppFields.Source) is { Length: > 0 } sourceText)
            {
                source = ExternalJson.TryParseSource(sourceText, out var parsed)
                    ? parsed
                    : throw new AppToolArgumentException(AppFields.Source, ExternalToolText.HistorySourceProblem);
            }

            DateTimeOffset? since = null;
            if (AppToolArgs.Text(args, AppFields.Since) is { Length: > 0 } sinceText)
            {
                since = DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at
                    : throw new AppToolArgumentException(AppFields.Since, ExternalToolText.HistorySinceProblem);
            }

            var connections = Connections(invocation);
            if (connections.Count == 0)
            {
                return AppToolOutputs.Deny(AgentErrorCodes.Level, ExternalToolText.HistoryNoConnection);
            }

            var found = new List<(HistoryEntry Entry, AgentGrantConnection Connection)>();
            foreach (var connection in connections)
            {
                var query = new HistoryQuery { ProfileId = connection.ProfileId, From = since, Limit = source is null ? limit : SourceScan };
                foreach (var entry in await history.SearchAsync(query, ct).ConfigureAwait(false))
                {
                    if (entry.ProfileId == connection.ProfileId && (source is null || entry.Source == source))
                    {
                        found.Add((entry, connection));
                    }
                }
            }

            var entries = new JsonArray();
            foreach (var (entry, connection) in found.OrderByDescending(f => f.Entry.ExecutedAt).ThenByDescending(f => f.Entry.Id).Take(limit))
            {
                entries.Add(Entry(entry, connection, invocation.Grant));
            }

            return AgentToolOutput.Ok(new JsonObject { ["entries"] = entries });
        });

    /// <summary>
    /// 이력을 읽을 접속: <see cref="AppToolInvocation.Connection"/>(다리가 <c>connection</c> 인자나 하나뿐인 접속으로 푼 것)이 있으면 그 접속,
    /// 없으면 허가의 모든 접속. 둘 다 수준 Metadata 미만은 뺀다.
    /// </summary>
    private static List<AgentGrantConnection> Connections(AppToolInvocation invocation)
    {
        if (AppToolArgs.Text(invocation.Arguments, AgentFields.Connection) is { Length: > 0 } alias && invocation.Connection is null)
        {
            // 다리가 풀지 못한 별칭(허가 밖)이면 허가에서 직접 찾는다. 없으면 빈 목록 → 거부.
            return invocation.Grant.FindConnection(alias) is { Level: >= AiAccessLevel.Metadata } named ? [named] : [];
        }

        IEnumerable<AgentGrantConnection> all = invocation.Connection is { } one ? [one] : invocation.Grant.Connections;
        return [.. all.Where(c => c.Level >= AiAccessLevel.Metadata)];
    }

    private JsonObject Entry(HistoryEntry entry, AgentGrantConnection connection, AgentGrantView grant)
    {
        var text = AgentGrantPolicy.MasksLiterals(grant, connection.Environment) ? Mask(entry.SqlText) : entry.SqlText;
        text = ExternalJson.Clip(text, MaxSqlChars, out var clipped);
        var json = new JsonObject
        {
            ["at"] = entry.ExecutedAt.ToString("O", CultureInfo.InvariantCulture),
            ["connection"] = connection.Alias,
            ["source"] = ExternalJson.SourceName(entry.Source),
            ["status"] = ExternalJson.StatusName(entry.Status),
        };
        if (entry.ElapsedMs is { } elapsed)
        {
            json["elapsed_ms"] = elapsed;
        }

        json["sql_text"] = text;
        if (clipped || entry.SqlTruncated)
        {
            json["sql_truncated"] = true;
        }

        if (entry.ErrorCode is { Length: > 0 } code)
        {
            json["error_code"] = code;
        }

        return json;
    }

    /// <summary>운영 이력 글: 리터럴·주석을 자리표시로. 실패하면 글 전체를 가린다(닫힌 쪽).</summary>
    private string Mask(string sql)
    {
        try
        {
            return masker.MaskPredicate(sql, masker.CreateVault(string.Empty));
        }
        catch (Exception)
        {
            return ISqlLiteralMasker.RedactedPlaceholder;
        }
    }
}
