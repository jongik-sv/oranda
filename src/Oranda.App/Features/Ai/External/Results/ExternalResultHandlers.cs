using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Grid;

namespace Oranda.App.Features.Ai.External.Results;

/// <summary>
/// <c>get_result</c>(설계서 §5.3, 결정 E-16): 탭 결과 그리드에 <b>이미 가져온</b> 행만 읽는다. 더 가져오지 않는다 —
/// <see cref="ResultGridModel.TryGetValue"/> 는 메모리에 있는 쪽만 읽고, 없으면 거기서 멈춘다(<see cref="ResultGridModel.GetCellText"/> 는 쪽 요청을 걸므로 쓰지 않는다).
/// 행 순서는 사람이 보는 순서(정렬·거르기 뒤)다. 값 글자는 그리드와 같은 표시 규칙(<see cref="CellValueFormatter"/>)이다.
/// 탭의 접속이 수준을 정한다: Query 미만 거부, 행 값은 QueryRows 에서만, 민감 열은 <c>***</c>. 허가 밖 접속 탭은 <c>locked</c>.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class GetResultHandler : IAppToolHandler
{
    public string ToolName => AppToolNames.GetResult;

    /// <summary>편집기 호스트. 기본은 프로세스의 호스트. 시험이 바꾼다.</summary>
    internal SqlEditorHost? Host { get; set; }

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = invocation.Arguments;
        var tabId = AppToolArgs.RequiredText(args, AppFields.Tab);
        var index = AppToolArgs.Number(args, AppFields.Index, -1, 0);
        var offset = AppToolArgs.Number(args, AppFields.Offset, 0, 0);
        var limit = AppToolArgs.Number(args, AppFields.Limit, AppToolValues.DefaultRows, 1, AppToolValues.MaxRows);
        return await ExternalToolSupport.OnUiAsync(() => Read(invocation.Grant, tabId, index, offset, limit)).ConfigureAwait(false);
    });

    /// <summary>탭을 찾았으면 그 탭 접속을 결과에 적는다(감사 줄의 접속·<c>data=local_ai</c>, 허가 안인지는 게이트가 다시 본다).</summary>
    private AgentToolOutput Read(AgentGrantView grant, string tabId, int index, int offset, int limit)
    {
        if (ExternalToolSupport.FindTab(Host ?? SqlEditorHost.Current, tabId) is not { } tab)
        {
            return AppToolOutputs.TabNotFound(tabId);
        }

        var output = Read(grant, tab, index, offset, limit);
        return tab.ConnectionProfileId is { } id ? output with { ProfileId = id } : output;
    }

    private static AgentToolOutput Read(AgentGrantView grant, SqlEditorViewModel tab, int index, int offset, int limit)
    {
        var profileId = tab.ConnectionProfileId;
        if (grant.IsLocked(profileId))
        {
            return AppToolOutputs.Locked();
        }

        var level = grant.LevelFor(profileId);
        if (level < AiAccessLevel.Query)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.Level, "Reading a result grid needs the Query level on the tab's connection.");
        }

        if (Pick(tab.ResultPane as ResultTabsViewModel, index) is not { Pane.Model: { } model } item)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NoResult, "This tab has no result grid at that index. Run the statement in Oranda first.");
        }

        // 값 수준 = 게이트와 같은 DataLevel(로컬 모델이면 운영 Query 수준에서도 행 값, D4·D5). 전에는 수준만 봐 운영 로컬 모델에 행 값이 없었다(w-it-p2 실측).
        var rowValues = AiAccessPolicy.DataLevel(level, !grant.MasksSensitive(profileId)) >= AiAccessLevel.QueryRows;
        var columns = model.Columns;
        // D-069: 접속의 민감 가림을 끄면 「항상 가리는 열」(비밀번호·주민번호·카드·계좌·주소·인증값)만 가린다. 로컬 모델은 지금처럼 처리자가 가리지 않는다.
        var narrow = !grant.MasksSensitiveColumns(profileId);
        var masked = columns.Select(c => grant.MasksSensitive(profileId) && SensitiveColumns.Hides(c.Name, narrow)).ToArray();
        var rows = new JsonArray();
        var fetched = 0;
        for (long r = offset; fetched < limit && r < model.RowCount; r++)
        {
            var row = new JsonArray();
            var resident = true;
            for (var c = 0; c < columns.Count && resident; c++)
            {
                resident = model.TryGetValue(r, c, out var value);
                if (rowValues)
                {
                    row.Add(value is null or DBNull ? null : masked[c] ? AppToolValues.Masked : CellValueFormatter.Format(columns[c], value, model.Display));
                }
            }

            if (!resident)
            {
                break;
            }

            fetched++;
            if (rowValues)
            {
                rows.Add(row);
            }
        }

        var result = new JsonObject
        {
            ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.DbTypeName })]),
        };
        if (rowValues)
        {
            result[AgentFields.RowValues] = rows;
        }

        result["fetched"] = fetched;
        result["more"] = offset + fetched < model.RowCount || !model.Source.IsComplete;
        result["statement_kind"] = item.Statement?.Kind.ToString().ToLowerInvariant();
        result["elapsed_ms"] = item.Elapsed is { } elapsed ? (long)elapsed.TotalMilliseconds : null;
        return AgentToolOutput.Ok(result);
    }

    /// <summary><paramref name="index"/> 가 음수면 그리드가 있는 마지막 결과, 아니면 그 자리(그리드가 없으면 null).</summary>
    private static ResultTabItem? Pick(ResultTabsViewModel? results, int index)
    {
        if (results is null)
        {
            return null;
        }

        if (index < 0)
        {
            return results.Tabs.LastOrDefault(t => t.Pane?.Model is not null);
        }

        return index < results.Tabs.Count ? results.Tabs[index] : null;
    }
}

/// <summary>
/// <c>get_last_error</c>: 창의 마지막 실행 오류(<see cref="ExecutionState.LastError"/>, 오류 고치기와 같은 원천). <c>tab</c> 을 주면 그 탭의 오류일 때만 돌려준다.
/// 오류를 낸 접속이 수준을 정한다: Metadata 미만 거부, 허가 밖 접속은 <c>locked</c>. 운영 접속은 오류 번호와 위치만 주고 글·문장은 가린다(설계서 §5.3).
/// 문장 글은 <c>sql_text</c> 칸에 넣어 게이트의 리터럴 가림을 탄다.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class GetLastErrorHandler(IServiceProvider services) : IAppToolHandler
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    public string ToolName => AppToolNames.GetLastError;

    /// <summary>편집기 호스트. 기본은 프로세스의 호스트. 시험이 바꾼다.</summary>
    internal SqlEditorHost? Host { get; set; }

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var tabId = AppToolArgs.Text(invocation.Arguments, AppFields.Tab);
        var bus = _services.GetRegisteredOrDefault<IEventBus>();
        return await ExternalToolSupport.OnUiAsync(() => Read(invocation.Grant, tabId, bus)).ConfigureAwait(false);
    });

    private AgentToolOutput Read(AgentGrantView grant, string? tabId, IEventBus? bus)
    {
        var state = ExecutionState.For(bus);
        if (tabId is not null)
        {
            if (ExternalToolSupport.FindTab(Host ?? SqlEditorHost.Current, tabId) is not { } tab)
            {
                return AppToolOutputs.TabNotFound(tabId);
            }

            if (state.LastErrorTab != tab.TabId)
            {
                return NoError();
            }
        }

        if (state.LastError is not { } error)
        {
            return NoError();
        }

        var profileId = state.LastErrorProfile;
        if (grant.IsLocked(profileId))
        {
            return AppToolOutputs.Locked();
        }

        if (grant.LevelFor(profileId) < AiAccessLevel.Metadata)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.Level, "Reading the last error needs at least the Metadata level on its connection.");
        }

        var masksLiterals = grant.MasksLiterals(profileId);
        var result = new JsonObject
        {
            ["code"] = string.IsNullOrEmpty(error.Code) ? null : error.Code,
            ["message"] = masksLiterals ? AppToolValues.Masked : error.Message,
        };
        if (!masksLiterals && grant.CanReadTabText(profileId) && state.LastErrorStatement is { } statement)
        {
            result[ExternalToolSupport.SqlTextField] = statement.Text;
        }

        result["line"] = error.Line;
        result["col"] = error.Column;
        result["at"] = ExternalToolSupport.Time(LastErrorTime(bus));
        return AgentToolOutput.Ok(result);
    }

    private static AgentToolOutput NoError() =>
        AppToolOutputs.Deny(AgentErrorCodes.NoResult, "There is no recent execution error for this tab or window.");

    /// <summary>메시지 창의 마지막 오류 줄 시각. 상태에는 시각이 없어 이것으로 갈음한다(없으면 null).</summary>
    private static DateTimeOffset? LastErrorTime(IEventBus? bus) =>
        ExecutionLog.For(bus).Messages.LastOrDefault(m => m.IsError)?.Time;
}
