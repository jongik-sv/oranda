using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli;

/// <summary>붙을 통로와 hello 에 낼 토큰.</summary>
/// <param name="PipeName">명명 파이프 이름(Windows) 또는 소켓 절대 경로(macOS·Linux).</param>
/// <param name="Token">실행 토큰(내장 실행) 또는 허가 토큰(외부 연결).</param>
/// <param name="External">연결 파일로 찾았다(밖에서 띄운 경우). 거짓이면 앱이 띄운 내장 실행.</param>
internal sealed record BridgeTarget(string PipeName, string Token, bool External)
{
    public override string ToString() => $"BridgeTarget {{ PipeName = {PipeName}, External = {External} }}";
}

/// <summary>
/// 통로와 토큰을 정한다. 환경 변수 <c>ORANDA_RUN_TOKEN</c> 이 있으면 앱이 띄운 내장 실행이다 — 지금처럼 <c>--pipe</c>(없으면 기본 이름)로 붙는다.
/// 없으면 밖에서 띄운 것이다 — 연결 파일을 찾아 통로·토큰을 읽는다(<see cref="EndpointLocator"/>). 토큰은 명령 인자로 받지 않는다(E-3).
/// </summary>
internal static class BridgeTargetResolver
{
    public static BridgeTarget Resolve(string? pipeOption, string? instanceOption, Func<string, string?> getEnvironmentVariable, AppOs os, EndpointLocator locator)
    {
        var runToken = getEnvironmentVariable(AgentRunToken.EnvironmentVariable);
        if (!string.IsNullOrEmpty(runToken))
        {
            var pipe = pipeOption ?? DefaultPipe.Resolve(getEnvironmentVariable, os)
                ?? throw new BridgeException(AgentErrorCodes.Usage, CliText.PipeRequired);
            return new BridgeTarget(pipe, runToken, External: false);
        }

        var instance = instanceOption ?? getEnvironmentVariable(EndpointLocator.InstanceEnvironmentVariable);
        var endpoint = locator.Choose(instance);
        return new BridgeTarget(pipeOption ?? endpoint.Endpoint.Name, endpoint.Token, External: true);
    }
}
