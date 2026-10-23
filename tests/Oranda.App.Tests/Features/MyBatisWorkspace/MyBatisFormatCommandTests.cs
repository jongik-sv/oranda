using Avalonia.Input;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.MyBatisWorkspace.Format;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Core.Text;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 「쿼리 포맷」 명령(<c>oranda.mybatis.formatStatement</c>): 사용 가능 조건, 일반 포맷과 나눠 쓰는 단축키(MyBatis 쿼리 편집기 = 이 명령, SQL·PL/SQL 편집기 = 일반 포맷),
/// 실행 취소 한 번에 원래 글, 실패하면 원문 유지와 알림, 읽기 전용·쿼리 탭이 아닌 탭은 건드리지 않음.
/// </summary>
public sealed class MyBatisFormatCommandTests
{
    private const string Id = "selectEmp";

    [Fact]
    public void Enabled_only_for_a_selected_statement_in_a_writable_mybatis_document()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        var cmd = registry.Find(MyBatisFormatCommands.FormatStatement);

        Assert.NotNull(cmd);
        Assert.Equal("mybatisDocFocus", cmd.When);
        Assert.True(registry.CanExecute(MyBatisFormatCommands.FormatStatement, Keys("mybatisDocActive", "mybatisStatementSelected")));
        Assert.False(registry.CanExecute(MyBatisFormatCommands.FormatStatement, Keys("mybatisDocActive", "mybatisStatementSelected", "readOnlyDoc")));
        Assert.False(registry.CanExecute(MyBatisFormatCommands.FormatStatement, Keys("mybatisDocActive")));
        Assert.False(registry.CanExecute(MyBatisFormatCommands.FormatStatement, Keys("sqlDocActive", "mybatisStatementSelected")));
    }

    [Theory]
    [MemberData(nameof(PresetsByPlatform))]
    public void Format_shortcut_goes_to_the_mybatis_command_only_in_the_mybatis_editor(string preset, bool macOS)
    {
        var map = KeymapLoader.LoadPreset(preset);
        var table = KeyBindingTable.Build(map, macOS);
        var binding = map.Bindings.First(b => b.Command == MyBatisFormatCommands.FormatStatement);
        var general = map.Bindings.Where(b => b.Command == FormatterProfilesCommands.FormatDocument).ToList();
        var chord = KeyChord.Parse(binding.KeysFor(macOS)[0], macOS).Select(g => (g.Key, g.KeyModifiers)).ToList();

        // 일반 포맷과 같은 키를 쓴다.
        Assert.All(general, g => Assert.Equal(binding.KeysFor(macOS), g.KeysFor(macOS)));

        var keys = new ContextKeyService();
        keys.SetFocusSlot("mybatisText");
        Assert.Equal(MyBatisFormatCommands.FormatStatement, table.Find(chord, keys).CommandId);
        keys.SetFocusSlot("sqlText");
        Assert.Equal(FormatterProfilesCommands.FormatDocument, table.Find(chord, keys).CommandId);
        keys.SetFocusSlot("plsql");
        Assert.Equal(FormatterProfilesCommands.FormatDocument, table.Find(chord, keys).CommandId);
    }

    [Fact]
    public Task Formats_the_statement_tab_and_one_undo_restores_it() => HeadlessHost.Run(async () =>
    {
        var (host, session, root) = await OpenEmpMapperAsync();
        try
        {
            var tab = session.OpenStatement(Id)!;
            host.Active = tab;
            var before = tab.Text;
            var notices = new List<string>();
            var handler = Handler(host, new UpperFormatter(), notices);

            await handler.ExecuteAsync(Context(), default);

            Assert.Equal(before.ToUpperInvariant(), tab.Text);
            Assert.Equal(before.ToUpperInvariant(), session.ElementOf(Id)!.Text);
            Assert.Empty(notices);
            tab.Document.UndoStack.Undo();
            Assert.Equal(before, tab.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Failure_keeps_the_original_and_shows_the_reason() => HeadlessHost.Run(async () =>
    {
        var (host, session, root) = await OpenEmpMapperAsync();
        try
        {
            var tab = session.OpenStatement(Id)!;
            host.Active = tab;
            var before = tab.Text;
            var notices = new List<string>();

            await Handler(host, new FailingFormatter(FormatFailure.MarkupUnsupported), notices).ExecuteAsync(Context(), default);

            Assert.Equal(before, tab.Text);
            Assert.False(tab.Document.UndoStack.CanUndo);
            var notice = Assert.Single(notices);
            Assert.Contains("원문 유지", notice, StringComparison.Ordinal);
            Assert.Contains("MyBatis 태그", notice, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Read_only_and_non_statement_tabs_are_left_alone() => HeadlessHost.Run(async () =>
    {
        var (host, session, root) = await OpenEmpMapperAsync();
        try
        {
            var formatter = new UpperFormatter();
            var notices = new List<string>();
            var handler = Handler(host, formatter, notices);

            var tab = session.OpenStatement(Id)!;
            host.Active = tab;
            tab.IsReadOnly = true;
            await handler.ExecuteAsync(Context(), default);

            // 쿼리 탭이 아닌 일반 SQL 탭은 「쿼리」 창에서 고른 문장이 있어도 포맷하지 않는다.
            var plain = new SqlEditorHost();
            var sql = plain.NewDocument("select a from t");
            plain.Active = sql;
            await Handler(plain, formatter, notices).ExecuteAsync(Context(), default);

            Assert.Equal(0, formatter.Calls);
            Assert.Empty(notices);
            Assert.Equal("select a from t", sql.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Shell_services_supply_the_real_formatter_and_options() => HeadlessHost.Run(async () =>
    {
        using var harness = new ShellHarness();
        Assert.NotNull(harness.Services.GetService<IMyBatisStatementFormatter>());

        var (host, session, root) = await OpenEmpMapperAsync();
        try
        {
            var tab = session.OpenStatement("selectEmpList")!;
            host.Active = tab;
            var before = tab.Text;
            var notices = new List<string>();

            // 셸이 쓰는 생성자: 포매터·옵션(설정의 기본 프로필)을 서비스에서 꺼낸다.
            var handler = new MyBatisFormatCommands(harness.Services)
            {
                Host = host,
                Notify = m =>
                {
                    notices.Add(m);
                    return Task.CompletedTask;
                },
            };
            await handler.ExecuteAsync(Context(), default);

            Assert.True(notices.Count == 0, string.Join(Environment.NewLine, notices));
            Assert.NotEqual(before, tab.Text);
            Assert.Contains("<foreach collection=\"deptIds\"", tab.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Tab_switched_while_reading_options_is_not_formatted() => HeadlessHost.Run(async () =>
    {
        var (host, session, root) = await OpenEmpMapperAsync();
        try
        {
            var tab = session.OpenStatement(Id)!;
            var other = session.OpenStatement("countEmp")!;
            host.Active = tab;
            var before = (tab.Text, other.Text);
            var formatter = new UpperFormatter();
            var handler = new MyBatisFormatCommands
            {
                Host = host,
                Formatter = formatter,
                OptionsProvider = _ =>
                {
                    host.Active = other;
                    return Task.FromResult(new FormatterOptions());
                },
                Notify = _ => Task.CompletedTask,
            };

            await handler.ExecuteAsync(Context(), default);

            Assert.Equal(0, formatter.Calls);
            Assert.Equal(before, (tab.Text, other.Text));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    public static TheoryData<string, bool> PresetsByPlatform()
    {
        var data = new TheoryData<string, bool>();
        foreach (var id in KeymapLoader.PresetIds)
        {
            data.Add(id, false);
            data.Add(id, true);
        }

        return data;
    }

    private static async Task<(SqlEditorHost Host, MyBatisWorkspaceSession Session, string Root)> OpenEmpMapperAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "oranda-mbfmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "EmpMapper.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks() };
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        return (host, Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis), root);
    }

    private static MyBatisFormatCommands Handler(SqlEditorHost host, ISqlFormatter formatter, List<string> notices) => new()
    {
        Host = host,
        Formatter = formatter,
        OptionsProvider = _ => Task.FromResult(new FormatterOptions()),
        Notify = m =>
        {
            notices.Add(m);
            return Task.CompletedTask;
        },
    };

    private static CommandContext Context() => new(MyBatisFormatCommands.FormatStatement, FakeContextKeys.AllTrue);

    private static KeySet Keys(params string[] on) => new(on);

    private sealed class KeySet(IEnumerable<string> on) : IContextKeys
    {
        private readonly HashSet<string> _on = new(on, StringComparer.Ordinal);

        public bool Evaluate(string? expression) =>
            string.IsNullOrEmpty(expression) || ContextKeyReasons.Evaluate(expression, _on.Contains);
    }

    private sealed class UpperFormatter : IMyBatisStatementFormatter
    {
        public int Calls { get; private set; }

        public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
        {
            Calls++;
            return new FormatResult(text.ToUpperInvariant(), new TextRange(0, text.Length));
        }
    }

    private sealed class FailingFormatter(FormatFailure failure) : IMyBatisStatementFormatter
    {
        public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
            => new(text, new TextRange(0, text.Length), failure) { Message = "시험" };
    }
}
