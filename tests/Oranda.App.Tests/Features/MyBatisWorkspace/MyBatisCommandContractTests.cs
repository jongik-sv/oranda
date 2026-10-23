using Oranda.App.Shell;
using Oranda.App.Tests.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-01-02 계약: MyBatis 명령이 등록돼 있고(design.md 4.1), MyBatis 문서(<c>mybatisDocActive</c>)에서
/// 일반 SQL 실행·포맷 명령이 꺼진다(design.md 4.4). 처리기 몸은 단정하지 않는다.
/// </summary>
public sealed class MyBatisCommandContractTests
{
    private static readonly string[] MyBatisCommandIds =
    [
        "oranda.mybatis.newStatement",
        "oranda.mybatis.renameStatement",
        "oranda.mybatis.deleteStatement",
        "oranda.mybatis.expandToSqlTab",
        "oranda.mybatis.expandMapper",
        "oranda.mybatis.runScenario",
        "oranda.mybatis.explainScenario",
        "oranda.mybatis.copyExpanded",
        "oranda.mybatis.copyExpandedWithValues",
        "oranda.mybatis.save",
        "oranda.mybatis.checkCombinations",
        "oranda.mybatis.applyAutoFix",
        "oranda.mybatis.convertWorkspace",
        "oranda.mybatis.openFolder",
        "oranda.mybatis.findStatement",
    ];

    private static readonly string[] GeneralSqlCommandIds =
    [
        "oranda.exec.runStatement",
        "oranda.exec.runForEdit",
        "oranda.plan.explain",
        "oranda.format.document",
    ];

    [Fact]
    public void Every_mybatis_command_is_registered()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        // 있는지만 본다(목록 전체 같음을 단정하지 않는다: 조정자가 승인한 명령이 더해져도 이 공용 시험을 고치지 않게).
        foreach (var id in MyBatisCommandIds)
        {
            Assert.NotNull(registry.Find(id));
        }
    }

    [Fact]
    public void Only_run_scenario_is_bound_to_the_mybatis_focus()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.Equal("mybatisDocFocus", registry.Find("oranda.mybatis.runScenario")!.When);
        Assert.All(
            MyBatisCommandIds.Where(id => id != "oranda.mybatis.runScenario"),
            id => Assert.Null(registry.Find(id)!.When));
    }

    [Theory]
    [MemberData(nameof(GeneralSql))]
    public void General_sql_commands_are_disabled_in_a_mybatis_document(string id)
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.False(registry.CanExecute(id, Keys("hasSession", "sqlDocActive", "mybatisDocActive")));
    }

    [Theory]
    [MemberData(nameof(GeneralSql))]
    public void General_sql_commands_stay_enabled_in_a_plain_sql_document(string id)
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.True(registry.CanExecute(id, Keys("hasSession", "sqlDocActive")));
    }

    [Fact]
    public void Placeholder_menu_commands_stay_disabled_until_the_feature_lane_turns_its_ready_key_on()
    {
        // R24: 기능 레인이 머지되기 전에는 빈 처리기 메뉴를 누를 수 없다(준비 키는 TSK-03-07·03-05 가 켠다).
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.False(registry.CanExecute("oranda.mybatis.openFolder", Keys("sqlDocActive")));
        Assert.False(registry.CanExecute("oranda.mybatis.convertWorkspace", Keys("sqlWorkspaceActive")));
        Assert.True(registry.CanExecute("oranda.mybatis.openFolder", Keys("mybatisFolderReady")));
        Assert.True(registry.CanExecute("oranda.mybatis.convertWorkspace", Keys("sqlWorkspaceActive", "mybatisConvertReady")));
    }

    public static IEnumerable<object[]> GeneralSql() => GeneralSqlCommandIds.Select(id => new object[] { id });

    private static KeySet Keys(params string[] on) => new(on);

    private sealed class KeySet(IEnumerable<string> on) : IContextKeys
    {
        private readonly HashSet<string> _on = new(on, StringComparer.Ordinal);

        public bool Evaluate(string? expression) =>
            string.IsNullOrEmpty(expression) || ContextKeyReasons.Evaluate(expression, _on.Contains);
    }
}
