using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Report;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>Codex 드라이버가 새 대화의 첫 차례에 내는 안내(미검증 버전 → 지시문 파일 순서)가 구조 결과·이어 말하기 답변·레포트에 섞이지 않는지 확인한다.</summary>
public class AiSystemCliCodexNoticeTests
{
    private const string VersionNotice = "[안내] 이 버전의 Codex CLI 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n";
    private const string InstructionsNotice = "참고: /home/x/.codex/AGENTS.md 의 지시문이 Codex 에 함께 전달됩니다(Oranda 가 막을 수 없습니다).\n";

    [Fact]
    public void Both_notices_stay_in_the_first_turn_and_never_reach_the_report_follow_up_answers()
    {
        var first = new TuningResult("처음 결론", [], [], []);
        var source = new AiTuningReportSource(
            new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), null, null, false, "SELECT 1", [new AiTuningReportTurn(0, first, [])]);
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");

        vm.BeginTurn("이 문장 튜닝");
        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta(VersionNotice));
        vm.Apply(new AiTextDelta(InstructionsNotice));
        vm.Apply(new AiFinal(null, null, "c1"));

        vm.BeginTurn("인덱스는요?");
        vm.Apply(new AiStarted("c1"));
        vm.Apply(new AiTextDelta("새 인덱스가 필요합니다."));
        vm.Apply(new AiFinal("새 인덱스가 필요합니다.", null, "c1"));

        var input = AiTuningReportAssembler.Build(source, vm.Title, vm.Entries);

        var followUp = Assert.Single(input.FollowUps);
        Assert.Equal("새 인덱스가 필요합니다.", followUp.Answer);
        Assert.DoesNotContain("[안내]", followUp.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTS.md", followUp.Answer, StringComparison.Ordinal);
        Assert.Equal("c1", vm.ResumeId);
    }
}
