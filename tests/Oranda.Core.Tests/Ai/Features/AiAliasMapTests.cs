using Oranda.Core.Ai.Features;

namespace Oranda.Core.Tests.Ai.Features;

/// <summary>사용자명·머신 이름 자리표시(결정 AE-3).</summary>
public class AiAliasMapTests
{
    [Fact]
    public void The_same_value_gets_the_same_placeholder_and_kinds_count_separately()
    {
        var map = new AiAliasMap();

        Assert.Equal("USER1", map.Alias(AiAliasKind.User, "SCOTT"));
        Assert.Equal("USER2", map.Alias(AiAliasKind.User, "HR"));
        Assert.Equal("USER1", map.Alias(AiAliasKind.User, "scott"));
        Assert.Equal("HOST1", map.Alias(AiAliasKind.Host, "PC-01"));
        Assert.Equal("PROGRAM1", map.Alias(AiAliasKind.Program, "app.exe"));
        Assert.Equal("MODULE1", map.Alias(AiAliasKind.Module, "ORDERS"));
    }

    [Fact]
    public void Empty_values_stay_empty()
    {
        var map = new AiAliasMap();

        Assert.Null(map.Alias(AiAliasKind.User, null));
        Assert.Null(map.Alias(AiAliasKind.User, "  "));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void Restore_puts_the_values_back_by_whole_word_only()
    {
        var map = new AiAliasMap();
        for (var i = 0; i < 10; i++)
        {
            map.Alias(AiAliasKind.User, "U" + i);
        }

        Assert.Equal("U0 와 U9, USER11, XUSER1", map.Restore("USER1 와 USER10, USER11, XUSER1"));
    }
}
