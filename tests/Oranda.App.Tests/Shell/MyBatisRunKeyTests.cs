using Avalonia.Input;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-09-06(D-021): Windows·Linux 키맵 프리셋 4종 모두 MyBatis 문서(mybatisText 자리)의 Ctrl+Enter 가 시나리오 실행이고 F9 도 남는다.
/// SQL 문서의 Ctrl+Enter 는 지금처럼 현재 문장 실행, PL/SQL 편집기는 PL/SQL 실행이다. 포커스 자리는 한 순간에 하나라
/// (FocusScopes) 디스패처 규칙을 바꾸지 않고 키맵 한 줄로 된다. macOS 키는 그대로다.
/// </summary>
public class MyBatisRunKeyTests
{
    private const string RunScenario = "oranda.mybatis.runScenario";
    private const string RunStatement = "oranda.exec.runStatement";

    public static TheoryData<string> Presets
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in KeymapLoader.PresetIds)
            {
                data.Add(id);
            }

            return data;
        }
    }

    private static readonly (Key, KeyModifiers)[] CtrlEnter = [(Key.Enter, KeyModifiers.Control)];

    private static readonly (Key, KeyModifiers)[] F9 = [(Key.F9, KeyModifiers.None)];

    private static ContextKeyService Focus(string slot)
    {
        var keys = new ContextKeyService();
        keys.SetFocusSlot(slot);
        return keys;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Ctrl_enter_runs_the_scenario_in_a_mybatis_document_and_the_statement_in_a_sql_document(string preset)
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: false);

        Assert.Equal(RunScenario, table.Find(CtrlEnter, Focus("mybatisText")).CommandId);
        Assert.Equal(RunStatement, table.Find(CtrlEnter, Focus("sqlText")).CommandId);
        Assert.Equal("oranda.plsql.run", table.Find(CtrlEnter, Focus("plsql")).CommandId);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void F9_still_runs_the_scenario_in_a_mybatis_document(string preset)
    {
        var map = KeymapLoader.LoadPreset(preset);
        var table = KeyBindingTable.Build(map, macOS: false);

        Assert.Equal(RunScenario, table.Find(F9, Focus("mybatisText")).CommandId);

        // SQL 문서의 F9 는 프리셋 그대로(기본·오렌지·PL/SQL Developer = 편집 모드 실행, SQL Developer = 문장 실행).
        var sqlF9 = map.Bindings.First(b => b.When == "sqlDocFocus" && b.Keys.Contains("F9")).Command;
        Assert.Equal(sqlF9, table.Find(F9, Focus("sqlText")).CommandId);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Run_scenario_binds_ctrl_enter_first_and_f9_on_windows(string preset)
    {
        var binding = KeymapLoader.LoadPreset(preset).Bindings.Single(b => b.Command == RunScenario);

        Assert.Equal("mybatisDocFocus", binding.When);
        Assert.Equal(["Primary+Enter", "F9"], binding.Keys);

        // 메뉴·툴팁 단축키 글은 첫 키를 보인다.
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: false);
        Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Control), table.SingleGesture(RunScenario));
        Assert.Equal("Ctrl+Enter", table.ShortcutText(RunScenario));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("orange5")]
    [InlineData("plsqldev")]
    public void Mac_keys_are_unchanged(string preset)
    {
        var binding = KeymapLoader.LoadPreset(preset).Bindings.Single(b => b.Command == RunScenario);
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: true);

        Assert.Equal(["Ctrl+Enter", "F9"], binding.Mac);
        Assert.Equal(RunScenario, table.Find([(Key.Enter, KeyModifiers.Control)], Focus("mybatisText")).CommandId);
        Assert.Equal(KeyMatchKind.None, table.Find([(Key.Enter, KeyModifiers.Meta)], Focus("mybatisText")).Kind); // ⌘↩ 는 MyBatis 문서에 묶지 않는다(지금 그대로)
        Assert.Equal(RunStatement, table.Find([(Key.Enter, KeyModifiers.Meta)], Focus("sqlText")).CommandId);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Ctrl_enter_is_not_swallowed_in_a_mybatis_document_when_the_scenario_command_is_turned_off(string preset)
    {
        // 기능 게이트가 MyBatis 를 끄면(TRD 10.7.1) 그 바인딩은 없는 것으로 본다. 문장 실행이 대신 잡지도 않는다(자리가 다르다).
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: false);

        var match = table.Find(CtrlEnter, Focus("mybatisText"), id => id != RunScenario);

        Assert.Equal(KeyMatchKind.None, match.Kind);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Other_ctrl_enter_bindings_do_not_overlap_the_mybatis_slot(string preset)
    {
        // 전역(when 없음)이나 editorFocus 처럼 mybatisText 를 품는 다른 Ctrl+Enter 바인딩이 있으면 충돌이다(commands.md 2.3).
        var ctrlEnter = KeymapLoader.LoadPreset(preset).Bindings
            .Where(b => b.Keys.Contains("Primary+Enter") && b.Command != RunScenario)
            .ToList();

        Assert.NotEmpty(ctrlEnter);
        Assert.All(ctrlEnter, b => Assert.False(FocusScopes.Overlaps(b.When, "mybatisDocFocus"), $"{b.Command}({b.When ?? "전역"})"));
    }

    [Fact]
    public async Task Dispatcher_runs_the_scenario_for_ctrl_enter_in_a_mybatis_document_and_the_statement_in_a_sql_document()
    {
        var registry = new CommandRegistry([]);
        var scenario = new Counter();
        var statement = new Counter();
        using var r1 = registry.Register(Def(RunScenario), scenario);
        using var r2 = registry.Register(Def(RunStatement), statement);
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS: false);

        var mybatis = new KeyDispatcher(() => table, registry, Focus("mybatisText"));
        Assert.True(mybatis.Handle(Key.Enter, KeyModifiers.Control));
        await mybatis.LastRun!;
        Assert.True(mybatis.Handle(Key.F9, KeyModifiers.None));
        await mybatis.LastRun!;

        var sql = new KeyDispatcher(() => table, registry, Focus("sqlText"));
        Assert.True(sql.Handle(Key.Enter, KeyModifiers.Control));
        await sql.LastRun!;

        Assert.Equal(2, scenario.Count);
        Assert.Equal(1, statement.Count);
    }

    private static CommandDefinition Def(string id) =>
        new() { Id = id, Title = id, Category = CommandCategory.Sql, Placements = ["menu:sql/x@10"] };

    private sealed class Counter : ICommandHandler
    {
        public int Count { get; private set; }

        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
