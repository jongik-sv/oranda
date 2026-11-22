using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Language.Errors;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>처리자가 낸 거부(코드 + 영어 한 문장). <see cref="ExternalDbHandlerBase"/> 가 <see cref="AgentToolOutput.Error(string, string)"/> 로 바꾼다.</summary>
internal sealed class ExternalToolException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// 외부 연결의 Host Db 도구(메타 6개·<c>query</c>) 공통. 게이트가 수준·범위·감사·가림을 마친 뒤 부른다(<see cref="IAgentToolHandler"/>).
/// 실패는 계약의 오류 코드로 돌려준다: 인자 오류 = <c>invalid_arguments</c>, 처리자 거부 = 그 코드, DB 오류 = <c>tool_failed</c> + 오류 번호만(원문은 로그 쪽 <see cref="AgentToolOutput.LogDetail"/>).
/// </summary>
internal abstract class ExternalDbHandlerBase : IAgentToolHandler
{
    public abstract string ToolName { get; }

    public async ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        try
        {
            var result = await RunAsync(invocation, ct).ConfigureAwait(false);
            return new AgentToolOutput { Result = result, SqlIds = SqlIdsOf(result) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 취소 중에 드라이버가 낸 오류(ORA-01013 등)는 취소로 다룬다.
            throw new OperationCanceledException(ct);
        }
        catch (AppToolArgumentException ex)
        {
            return AgentToolOutput.Error(AgentErrorCodes.InvalidArguments, ex.Message);
        }
        catch (ExternalToolException ex)
        {
            return AgentToolOutput.Error(ex.Code, ex.Message) with { LogDetail = Detail(ex.InnerException) };
        }
        catch (Exception ex)
        {
            return AgentToolOutput.Error(AgentErrorCodes.ToolFailed, ExternalToolText.LookupFailed(DbErrorClassification.CodeOf(ex))) with { LogDetail = Detail(ex) };
        }
    }

    protected abstract Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct);

    /// <summary>이 결과로 조회해도 되게 된 sql_id. 문장을 실행한 도구만 덮어쓴다.</summary>
    protected virtual IReadOnlyList<string> SqlIdsOf(JsonNode result) => [];

    /// <summary>이름 풀기 기본 스키마: 실행 범위의 현재 스키마, 없으면 접속 사용자(null).</summary>
    protected static string? DefaultSchema(AgentToolInvocation inv) =>
        string.IsNullOrWhiteSpace(inv.Session.Context.CurrentSchema) ? null : inv.Session.Context.CurrentSchema;

    private static string? Detail(Exception? ex) => ex is null ? null : ex.GetType().Name + ": " + ex.Message;
}
