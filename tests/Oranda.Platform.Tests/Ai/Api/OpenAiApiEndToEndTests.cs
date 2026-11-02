using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Api;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>
/// 실제 OpenAI 형식 서버를 부르는 끝까지 확인. 환경 변수 <c>ORANDA_AI_E2E_BASE_URL</c> 과 <c>ORANDA_AI_E2E_MODEL</c> 이 있을 때만 돈다
/// (키가 필요한 서버는 <c>ORANDA_AI_E2E_API_KEY</c>). 이미지 + 도구 + 이어 말하기 + 구조 결과를 차례로 확인한다. 주소는 코드에 두지 않는다.
/// </summary>
public sealed class OpenAiApiEndToEndTests
{
    // 빨간색 1x1 PNG.
    private static readonly byte[] RedPixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"color":{"type":"string"}},"required":["color"]}""").RootElement.Clone();

    [ApiEndToEndFact]
    public async Task A_real_server_handles_an_image_a_tool_a_resumed_turn_and_a_structured_result()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_BASE_URL")!;
        var model = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_MODEL")!;
        var key = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_API_KEY");
        var profile = new AiLlmProfile { Id = Guid.NewGuid(), Name = "e2e", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = baseUrl, Model = model };
        var gate = new FakeGate();
        using var driver = new OpenAiApiDriver(gate);
        var route = new AiRoute(profile, string.IsNullOrWhiteSpace(key) ? null : key);

        var first = await RunAsync(driver, route, new AiRequest(
            "첨부한 이미지의 주된 색을 한 단어로 말하고, 반드시 describe_object 도구를 object 인자 TB_ORDER 로 한 번 불러 결과를 확인한 뒤 답하세요.",
            Context(),
            Session: NewSession(),
            Images: [new AiImageAttachment("image/png", RedPixel)]));
        Assert.Contains(first, e => e is AiToolCall { ToolName: "describe_object" });
        var final = Assert.IsType<AiFinal>(first.Last());
        Assert.False(string.IsNullOrWhiteSpace(final.Text));

        var second = await RunAsync(driver, route, new AiRequest("방금 본 이미지의 색을 다시 한 단어로 말하세요.", Context(), final.ResumeId, Session: NewSession()));
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<AiFinal>(second.Last()).Text));

        var third = await RunAsync(driver, route, new AiRequest("이미지의 주된 색을 결과로 제출하세요.", Context(), final.ResumeId, Schema, Session: NewSession()));
        Assert.True(Assert.IsType<AiFinal>(third.Last()).Result?.TryGetProperty("color", out _));
    }

    private static AiContext Context() => new("dev", null, AiAccessLevel.Plan);

    private static AgentSession NewSession() =>
        new(new AgentRunScope("run-e2e", new ConnectionProfile("dev", "scott", "h/s"), Context()));

    private static async Task<List<AiEvent>> RunAsync(OpenAiApiDriver driver, AiRoute route, AiRequest request)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, route, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.DoesNotContain(events, e => e is AiError);
        return events;
    }
}

public sealed class ApiEndToEndFactAttribute : FactAttribute
{
    public ApiEndToEndFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_AI_E2E_BASE_URL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_AI_E2E_MODEL")))
        {
            Skip = "ORANDA_AI_E2E_BASE_URL 과 ORANDA_AI_E2E_MODEL 이 있을 때만 실제 서버를 부른다.";
        }
    }
}
