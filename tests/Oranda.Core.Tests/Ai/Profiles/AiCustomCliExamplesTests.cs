using Oranda.Core.Ai.Profiles;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>기타 CLI 명령 틀 표: 모든 틀이 분해기를 통과하고, 글을 인자 자리로 받으며, 위험한 옵션이 들어 있지 않다.</summary>
public class AiCustomCliExamplesTests
{
    public static IEnumerable<object[]> Ids => AiCustomCliExamples.All.Select(e => new object[] { e.Id });

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_example_passes_the_command_parser_with_a_bare_executable_name(string id)
    {
        var example = AiCustomCliExamples.All.Single(e => e.Id == id);

        var result = AiCustomCliCommand.Parse(example.Command);

        Assert.True(result.Succeeded, $"{example.Command} → {result.Error}");
        var command = result.Command!;
        Assert.DoesNotContain(Path.DirectorySeparatorChar, command.Executable); // 이름만(PATH 에서 찾는다)
        Assert.True(command.UsesPromptArgument); // 글을 {prompt} 인자로 받는 1회 실행 형태
        Assert.False(command.UsesPromptFile);
        Assert.Empty(command.Environment); // 비밀 값을 틀에 두지 않는다
        Assert.Empty(command.AutoApproveOptions); // 자동 승인 옵션은 틀에 넣지 않는다
        Assert.DoesNotContain("://", example.Command); // 주소를 틀에 두지 않는다
    }

    [Fact]
    public void Examples_have_unique_ids_and_names_and_the_confirmed_ones_come_first()
    {
        var all = AiCustomCliExamples.All;

        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(all.Count, all.Select(e => e.Name).Distinct().Count());
        Assert.All(all, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        var firstUnconfirmed = all.ToList().FindIndex(e => e.Verification == AiCustomCliVerification.Unconfirmed);
        Assert.All(all.Skip(firstUnconfirmed), e => Assert.Equal(AiCustomCliVerification.Unconfirmed, e.Verification));
        Assert.True(firstUnconfirmed > 0); // 확인됨 틀이 하나는 있다
    }

    [Fact]
    public void A_note_always_comes_with_its_english_text()
    {
        Assert.All(AiCustomCliExamples.All, e => Assert.Equal(e.NoteKo is null, e.NoteEn is null));
        Assert.NotNull(AiCustomCliExamples.All.Single(e => e.Id == "zcode").NoteKo); // 기본이 자동 승인이라 분해기가 못 잡는 위험을 알린다
    }

    [Fact]
    public void Codex_template_carries_the_flag_the_empty_folder_needs()
    {
        var codex = AiCustomCliCommand.Parse(AiCustomCliExamples.All.Single(e => e.Id == "codex").Command).Command!;

        Assert.Equal(["exec", "--skip-git-repo-check", "{prompt}"], codex.Arguments);
    }
}
