using System.Text.Json;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 실제 파이프라인(claude stream-json → CliDriver)이 낸 AiEvent 목록을 대화 창에 흘려 본다(ai-bridge 녹화본 e2e-ai-events.json 을 줄여 옮김).
/// 호스트·경로 같은 사용자 환경 정보는 없다.
/// </summary>
public class AiChatRecordedEventsTests
{
    private const string Recorded = """
        [
          {"type":"AiStarted","e":{"ResumeId":"5cda83e7-77fc-4507-89bb-223c143fac3f"}},
          {"type":"AiToolCall","e":{"CallId":"toolu_01","ToolName":"describe_object","ArgumentsSummary":"{\"object\":\"EMP\"}"}},
          {"type":"AiToolResult","e":{"CallId":"toolu_01","ToolName":"describe_object","Size":60,"Judgement":"Allowed","Note":null}},
          {"type":"AiTextDelta","e":{"Text":"The `describe_object` call on EMP returned `num_rows` = **1234**."}},
          {"type":"AiUsage","e":{"InputTokens":78661,"OutputTokens":227,"CostUsd":0.00240068}},
          {"type":"AiFinal","e":{"Text":null,"Result":{"rows":1234},"ResumeId":"5cda83e7-77fc-4507-89bb-223c143fac3f"}}
        ]
        """;

    private static List<AiEvent> Parse()
    {
        var events = new List<AiEvent>();
        foreach (var item in JsonDocument.Parse(Recorded).RootElement.EnumerateArray())
        {
            var e = item.GetProperty("e");
            events.Add(item.GetProperty("type").GetString() switch
            {
                "AiStarted" => new AiStarted(e.GetProperty("ResumeId").GetString()),
                "AiToolCall" => new AiToolCall(e.GetProperty("CallId").GetString()!, e.GetProperty("ToolName").GetString()!, e.GetProperty("ArgumentsSummary").GetString()!),
                "AiToolResult" => new AiToolResult(e.GetProperty("CallId").GetString()!, e.GetProperty("ToolName").GetString()!, e.GetProperty("Size").GetInt32(), Enum.Parse<AiToolJudgement>(e.GetProperty("Judgement").GetString()!)),
                "AiTextDelta" => new AiTextDelta(e.GetProperty("Text").GetString()!),
                "AiUsage" => new AiUsage(e.GetProperty("InputTokens").GetInt64(), e.GetProperty("OutputTokens").GetInt64(), e.GetProperty("CostUsd").GetDecimal()),
                "AiFinal" => new AiFinal(e.GetProperty("Text").GetString(), e.GetProperty("Result").Clone(), e.GetProperty("ResumeId").GetString()),
                var other => throw new InvalidOperationException("알 수 없는 사건: " + other),
            });
        }

        return events;
    }

    private static async IAsyncEnumerable<AiEvent> Stream(IEnumerable<AiEvent> events)
    {
        foreach (var e in events)
        {
            yield return e;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Recorded_pipeline_events_render_as_one_judged_tool_card_and_the_answer()
    {
        var vm = new AiChatViewModel();
        vm.BeginTurn("EMP 가 몇 행인지 알려 줘");
        await vm.RunAsync(Stream(Parse()), CancellationToken.None);

        var card = Assert.Single(vm.Entries.OfType<ChatToolEntry>());
        Assert.Equal(("describe_object", "허용", true), (card.Name, card.Verdict, card.Done));
        Assert.Contains("1234", vm.Entries.OfType<ChatAssistantEntry>().Single().Text, StringComparison.Ordinal);
        Assert.Equal("5cda83e7-77fc-4507-89bb-223c143fac3f", vm.ResumeId);
        Assert.False(vm.IsRunning);
    }
}
