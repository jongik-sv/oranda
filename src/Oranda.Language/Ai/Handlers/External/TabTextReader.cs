using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// 언어 도구(<c>check_sql</c>·<c>format_sql</c>)의 <c>tab</c> 인자: 편집기 레인의 <c>get_tab</c> 처리자를 그대로 불러 글을 얻는다.
/// 탭 글 읽기 허가(접속·운영·잠김)를 <c>get_tab</c> 한 곳에서만 판단하게 하려는 것이다. 처리자 목록은 호출 때 꺼낸다(처리자끼리 생성자에서 서로 기대지 않게).
/// </summary>
[OrandaService(typeof(TabTextReader))]
internal sealed class TabTextReader(IServiceProvider services)
{
    /// <summary><c>get_tab</c> 의 <c>max_chars</c> 최대.</summary>
    public const int MaxChars = 50_000;

    /// <summary>
    /// 탭 글을 읽는다. 성공하면 (글, null), 실패하면 (null, 오류 결과). 잘린 글은 실패로 본다(일부만 검사·서식하면 위치가 틀린다).
    /// </summary>
    public async ValueTask<(string? Text, AgentToolOutput? Error)> ReadAsync(AppToolInvocation invocation, string tab, string scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var handler = (services.GetService(typeof(IEnumerable<IAppToolHandler>)) as IEnumerable<IAppToolHandler> ?? [])
            .FirstOrDefault(h => h.ToolName == AppToolNames.GetTab);
        if (handler is null)
        {
            return (null, AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, ExternalToolText.TabReaderMissing));
        }

        var args = new JsonObject
        {
            [AppFields.Tab] = tab,
            [AppFields.Scope] = scope,
            [AppFields.MaxChars] = MaxChars,
        };
        var output = await handler.ExecuteAsync(invocation with { Arguments = args, Connection = null }, ct).ConfigureAwait(false);
        if (output.IsError)
        {
            return (null, output);
        }

        if (output.Result?[AppFields.Text] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return (null, AppToolOutputs.Deny(AgentErrorCodes.ToolFailed, ExternalToolText.TabReaderMissing));
        }

        return output.Result["truncated"] is JsonValue t && t.TryGetValue<bool>(out var truncated) && truncated
            ? (null, AppToolOutputs.Deny(AgentErrorCodes.ResultTooLarge, ExternalToolText.TabTooLong))
            : (text, null);
    }
}
