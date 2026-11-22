using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.External.Results;

/// <summary>상태·결과 도구 처리자(레인 E3)가 같이 쓰는 작은 도움 함수.</summary>
internal static class ExternalToolSupport
{
    /// <summary>SQL 글을 넣는 칸. 이 이름이어야 게이트의 리터럴 가림을 탄다(<see cref="AgentFields.SqlTextFields"/>).</summary>
    public const string SqlTextField = "sql_text";

    /// <summary>편집기·결과 모델은 UI 스레드 것이다. 다리 스레드에서 불리면 UI 스레드로 넘겨 읽는다. 앱이 없으면(시험) 바로 읽는다.</summary>
    public static async ValueTask<T> OnUiAsync<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess() ? read() : await Dispatcher.UIThread.InvokeAsync(read);
    }

    /// <summary>탭 id(<c>list_tabs</c> 의 <c>tab_id</c>, Guid 글 어느 모양이든)로 탭을 찾는다. 모르면 null.</summary>
    public static SqlEditorViewModel? FindTab(SqlEditorHost host, string? id)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Guid.TryParse(id, out var tabId) ? host.Tabs.FirstOrDefault(t => t.TabId == tabId) : null;
    }

    /// <summary>계약의 환경 글(<c>Development</c>·<c>Test</c>·<c>Production</c>, 연결 파일과 같은 enum 이름).</summary>
    public static string EnvironmentName(ConnectionEnvironment environment) => environment.ToString();

    /// <summary>계약의 수준 글(<c>Off</c>·<c>Metadata</c>·<c>Plan</c>·<c>Query</c>·<c>QueryRows</c>).</summary>
    public static string LevelName(AiAccessLevel level) => level.ToString();

    /// <summary>계약의 DBMS 글(<c>oracle</c>·<c>tibero</c>).</summary>
    public static string DbmsName(Dbms dbms) => dbms == Dbms.Tibero ? AppToolValues.DbmsNames.Tibero : AppToolValues.DbmsNames.Oracle;

    /// <summary>ISO 8601 시각(오프셋 포함) 또는 null.</summary>
    public static JsonNode? Time(DateTimeOffset? at) =>
        at is { } value ? JsonValue.Create(value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)) : null;

    /// <summary>
    /// 이 허가에서 보이는 도구 수: 최소 수준이 없는 도구 + 허용 접속 가운데 가장 높은 수준으로 쓸 수 있는 도구.
    /// 다리의 실제 도구 목록(A4)과 어긋나면 그쪽이 옳다. 이 값은 사람이 보는 참고값이다.
    /// </summary>
    public static int ToolCount(AgentGrantView grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var top = grant.Connections.Count == 0 ? AiAccessLevel.Off : grant.Connections.Max(c => c.Level);
        return AppTools.External.Count(t => !t.DevTestOnly && (t.MinimumLevel == AiAccessLevel.Off || (top != AiAccessLevel.Off && t.MinimumLevel <= top)));
    }
}
