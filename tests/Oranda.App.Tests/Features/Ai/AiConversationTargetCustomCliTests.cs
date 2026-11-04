using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>기타 CLI 가 Claude Code 와 같은 대상으로 판정되지 않고, 명령만 고쳐도 다른 대상이 되는지 확인한다.</summary>
public class AiConversationTargetCustomCliTests
{
    private static readonly AiCustomCli Mine = new() { Id = "c1", Name = "사내 CLI", Command = "mycli --quiet" };

    private static AiConversationTarget From(string? selection, params AiCustomCli[] clis) =>
        AiConversationTarget.From(new AiSettings { SystemCliId = selection, CustomClis = clis });

    [Fact]
    public void A_custom_cli_is_its_own_target_with_the_selection_id_and_command_hash()
    {
        var target = From("custom:c1", Mine);

        Assert.Equal("사내 CLI", target.Label);
        Assert.Equal("custom:c1", target.SystemCliId);
        Assert.Equal(AiCustomCliTarget.CommandHash(Mine.Command), target.CustomCliCommandHash);
        Assert.False(target.IsSameAs(From(null)));
        Assert.Equal(new Oranda.Core.Ai.Contracts.AiProfileExpectation(null, SystemCliId: "custom:c1", CustomCliCommandHash: target.CustomCliCommandHash), target.ToExpectation());
    }

    [Fact]
    public void Editing_only_the_command_makes_it_a_different_target()
    {
        var before = From("custom:c1", Mine);

        Assert.True(before.IsSameAs(From("custom:c1", Mine with { Name = "이름만 바뀜" })));
        Assert.False(before.IsSameAs(From("custom:c1", Mine with { Command = "mycli --auto" })));
    }

    [Fact]
    public void A_missing_row_is_an_unavailable_target_that_is_not_claude_code()
    {
        var gone = From("custom:c1");

        Assert.Equal("custom:c1", gone.SystemCliId);
        Assert.Null(gone.CustomCliCommandHash);
        Assert.False(gone.IsSameAs(From(null)));
        Assert.False(gone.IsSameAs(From("custom:c1", Mine)));
    }
}
