using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Report;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 시스템 CLI 가 새 대화의 첫 차례에 내는 미검증 버전 안내 한 줄(<see cref="AiTextDelta"/>)이 구조 결과 파싱, 이어 말하기 대화 기록,
/// 튜닝 레포트의 모델 답변에 섞이지 않는지 확인한다. 레포트는 첫 사용자 줄 뒤의 AI 글만 이어 말하기 답변으로 모으므로 안내는 첫 차례에만 낸다.
/// </summary>
public class AiSystemCliNoticeTests
{
    private const string Notice = "[안내] 이 버전의 Antigravity CLI 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n";

    [Fact]
    public void The_notice_in_the_first_turn_does_not_reach_the_report_follow_up_answers_and_the_final_text_does_not_replace_it()
    {
        var first = new TuningResult("처음 결론", [], [], []);
        var source = new AiTuningReportSource(
            new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), null, null, false, "SELECT 1", [new AiTuningReportTurn(0, first, [])]);
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");

        // 첫 차례: 안내 → 구조 결과(최종 글 없음)
        vm.BeginTurn("이 문장 튜닝");
        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta(Notice));
        vm.Apply(new AiFinal(null, null, "c1"));

        // 이어 말하기 차례: 안내 없음, 구조 결과 없는 말 답
        vm.BeginTurn("인덱스는요?");
        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta("새 인덱스가 필요합니다."));
        vm.Apply(new AiFinal("새 인덱스가 필요합니다.", null, "c1"));

        var input = AiTuningReportAssembler.Build(source, vm.Title, vm.Entries);

        Assert.Contains(vm.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("[안내]", StringComparison.Ordinal)); // 화면에는 남는다
        var followUp = Assert.Single(input.FollowUps);
        Assert.Equal("새 인덱스가 필요합니다.", followUp.Answer);
        Assert.DoesNotContain("[안내]", followUp.Answer, StringComparison.Ordinal);
        Assert.Equal("c1", vm.ResumeId); // 이어 말하기 id 는 AiStarted·AiFinal 만 정한다
    }

    [Fact]
    public void The_notice_stays_on_screen_when_the_final_text_arrives_after_the_deltas()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("대화");
        vm.BeginTurn("질문");

        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta(Notice));
        vm.Apply(new AiTextDelta("답변"));
        vm.Apply(new AiFinal("답변", null, "c1"));

        var answer = Assert.IsType<ChatAssistantEntry>(vm.Entries[^1]);
        Assert.StartsWith("[안내]", answer.Text, StringComparison.Ordinal);
        Assert.EndsWith("답변", answer.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n")]
    [InlineData("[안내] AI 가 도구를 쓰지 않고 답했으므로 실측 근거가 없습니다.\n\n")]
    public void The_tool_evidence_notices_of_the_first_turn_do_not_reach_the_report_nor_the_captured_structured_result(string notice)
    {
        var first = new TuningResult("처음 결론", [], [], []);
        var source = new AiTuningReportSource(
            new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), null, null, false, "SELECT 1", [new AiTuningReportTurn(0, first, [])]);
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        using var document = System.Text.Json.JsonDocument.Parse("""{"summary":"처음 결론","rewrites":[],"index_suggestions":[],"evidence":[]}""");
        var structured = document.RootElement.Clone();
        var firstTurn = new AiEvent[] { new AiStarted("c1"), new AiTextDelta(notice), new AiFinal(null, structured, "c1") };

        vm.BeginTurn("이 문장 튜닝");
        foreach (var e in firstTurn)
        {
            vm.Apply(e);
        }

        vm.BeginTurn("인덱스는요?");
        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta("새 인덱스가 필요합니다."));
        vm.Apply(new AiFinal("새 인덱스가 필요합니다.", null, "c1"));

        // AiTuningFlow.Capture 와 같은 선택: 안내 글 조각이 앞에 있어도 구조 결과는 AiFinal.Result 하나다.
        var captured = firstTurn.OfType<AiFinal>().Select(f => f.Result).Single(r => r is not null);
        Assert.Equal("처음 결론", captured!.Value.GetProperty("summary").GetString());

        var input = AiTuningReportAssembler.Build(source, vm.Title, vm.Entries);
        var followUp = Assert.Single(input.FollowUps);
        Assert.Equal("새 인덱스가 필요합니다.", followUp.Answer);
        Assert.DoesNotContain("[안내]", followUp.Answer, StringComparison.Ordinal);
        Assert.Contains(vm.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("[안내]", StringComparison.Ordinal));
    }
}
