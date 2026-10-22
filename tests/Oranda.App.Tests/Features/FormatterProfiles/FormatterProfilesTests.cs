using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.FormatterProfiles;

/// <summary>TSK-05-03 포매터 프로필 화면·미리보기·포맷 명령·공유 시험.</summary>
public class FormatterProfilesTests
{
    private static ISqlFormatter RealFormatter()
    {
        var type = typeof(Oranda.Language.LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new FormatterProfilesViewModel());
    }

    [Fact]
    public void Panel_and_commands_are_registered()
    {
        Assert.Equal(ToolPanelKind.Document, ShellTestHelpers.RequirePanel("oranda.panel.formatterProfiles").Kind);
        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in new[] { "oranda.format.document", "oranda.format.statement", "oranda.format.profiles" })
        {
            Assert.NotNull(reg.Find(id));
        }
    }

    [Fact]
    public void Option_rows_come_from_the_FormatterOptions_contract()
    {
        var paths = FormatterOptionSchema.All.Select(d => d.Path).ToList();

        Assert.Equal(29, paths.Count); // TSK-13-43: clauseLayout·lineBreaks.orderBy·align.comparison 추가
        foreach (var expected in new[] { "case.keyword", "indent.size", "commaPosition", "lineBreaks.maxLineWidth", "align.equals", "comments.maxBlankLines", "statementTerminator", "honorFormatterOff" })
        {
            Assert.Contains(expected, paths);
        }

        Assert.All(FormatterOptionSchema.All.Where(d => d.Kind == OptionKind.Choice), d => Assert.NotEmpty(d.Choices));
        Assert.DoesNotContain(FormatterOptionSchema.All, d => d.Label == d.Path); // 모든 옵션에 라벨이 있다
    }

    [Fact]
    public void Unknown_options_are_found()
    {
        var node = JsonNode.Parse("""{ "future": 1, "case": { "keyword": "Lower", "bogus": 2 } }""");
        Assert.Equal(["future", "case.bogus"], FormatterOptionSchema.FindUnknown(node));
    }

    private static async Task<(FormatterProfilesViewModel Vm, MemoryStore Store)> LoadedAsync()
    {
        var store = new MemoryStore();
        var vm = new FormatterProfilesViewModel(store, RealFormatter(), new MemorySettings());
        await vm.LoadAsync();
        return (vm, store);
    }

    [Fact]
    public async Task Changing_an_option_updates_the_preview_immediately()
    {
        var (vm, _) = await LoadedAsync();
        await vm.CopySelectedAsync(); // 기본 제공은 복사해서 고친다
        Assert.True(vm.IsEditable);
        Assert.Contains("SELECT", vm.PreviewText);

        vm.Rows.Single(r => r.Path == "case.keyword").ChoiceValue = nameof(LetterCase.Lower);

        Assert.Contains("select", vm.PreviewText);
        Assert.DoesNotContain("SELECT", vm.PreviewText);
        Assert.True(vm.IsDirty);

        var before = vm.PreviewText;
        vm.Rows.Single(r => r.Path == "commaPosition").ChoiceValue = nameof(CommaPosition.Leading);
        Assert.NotEqual(before, vm.PreviewText);
    }

    [Fact]
    public async Task Built_in_profiles_are_read_only_and_listed_first()
    {
        var (vm, _) = await LoadedAsync();

        Assert.Equal(["oranda-default", "orange5", "compact"], vm.Profiles.Select(p => p.Profile.Id));
        Assert.False(vm.IsEditable);
        Assert.All(vm.Rows, r => Assert.False(r.IsEditable));
        Assert.All(vm.Profiles, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public async Task Save_persists_edits_and_delete_removes()
    {
        var (vm, store) = await LoadedAsync();
        await vm.CopySelectedAsync();
        var id = vm.Selected!.Profile.Id;
        vm.ProfileName = "내 규칙";
        vm.Rows.Single(r => r.Path == "indent.size").NumberValue = 2;
        await vm.SaveAsync();

        var saved = store.Items.Single(p => p.Id == id);
        Assert.Equal("내 규칙", saved.Name);
        Assert.Equal(2, saved.Options.Indent.Size);
        Assert.False(vm.IsDirty);

        await vm.DeleteAsync();
        Assert.DoesNotContain(store.Items, p => p.Id == id);
    }

    [Fact]
    public async Task Set_default_writes_settings_and_marks_the_entry()
    {
        var store = new MemoryStore();
        var settings = new MemorySettings();
        var vm = new FormatterProfilesViewModel(store, RealFormatter(), settings);
        await vm.LoadAsync();
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "compact");

        await vm.SetDefaultAsync();

        Assert.Equal("compact", settings.Current.FormatterProfileId);
        Assert.True(vm.Profiles.Single(p => p.Profile.Id == "compact").IsDefault);
        Assert.False(vm.Profiles.Single(p => p.Profile.Id == "oranda-default").IsDefault);
    }

    [Fact]
    public async Task Import_tells_about_unknown_options_and_renamed_profile()
    {
        var (vm, store) = await LoadedAsync();
        var file = Path.Combine(Path.GetTempPath(), "oranda-fp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(file, """{ "schemaVersion": 1, "id": "team", "name": "Oranda 기본", "options": { "future": 1, "indent": { "size": 2 } } }""");

            await vm.ImportAsync(file);

            Assert.Contains("future", vm.StatusText);
            Assert.Contains("무시", vm.StatusText);
            Assert.Contains("Oranda 기본 (2)", vm.StatusText); // 이름 충돌 → 새 이름
            Assert.Equal(4, store.Items.Count);
            Assert.Equal("Oranda 기본 (2)", vm.Selected!.Profile.Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Export_then_import_gives_same_format_result()
    {
        var (vm, store) = await LoadedAsync();
        await vm.CopySelectedAsync();
        vm.Rows.Single(r => r.Path == "case.keyword").ChoiceValue = nameof(LetterCase.Lower);
        vm.Rows.Single(r => r.Path == "commaPosition").ChoiceValue = nameof(CommaPosition.Leading);
        await vm.SaveAsync();
        var original = vm.CurrentOptions;
        var file = Path.Combine(Path.GetTempPath(), "oranda-fp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await vm.ExportAsync(file);
            var other = new MemoryStore();
            var pcB = new FormatterProfilesViewModel(other, RealFormatter(), new MemorySettings());
            await pcB.LoadAsync();
            await pcB.ImportAsync(file);

            Assert.Equal(original, pcB.CurrentOptions);
            Assert.Equal(vm.PreviewText, pcB.PreviewText);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static SqlEditorViewModel Tab(string text) => new(Guid.NewGuid(), text);

    [Fact]
    public void Apply_formats_whole_document_as_one_undo_step()
    {
        var tab = Tab("select a,b from t where x=1");
        var text0 = tab.Text;

        var changed = FormatterProfilesCommands.Apply(tab, RealFormatter(), new FormatterOptions(), null, out var message);

        Assert.True(changed, message);
        Assert.NotEqual(text0, tab.Text);
        Assert.Contains("SELECT", tab.Text);
        tab.Document.UndoStack.Undo();
        Assert.Equal(text0, tab.Text);
    }

    [Fact]
    public void Apply_with_selection_changes_only_that_range()
    {
        var tab = Tab("select 1 from dual;\nselect a,b from t where x=1;");
        var second = tab.Text.IndexOf("select a", StringComparison.Ordinal);

        FormatterProfilesCommands.Apply(tab, RealFormatter(), new FormatterOptions(), new TextRange(second, tab.Text.Length - second), out _);

        Assert.StartsWith("select 1 from dual;\n", tab.Text);
        Assert.Contains("SELECT", tab.Text[second..]);
    }

    [Fact]
    public void Failure_keeps_original_and_explains()
    {
        var tab = Tab("select 1 from dual");

        var changed = FormatterProfilesCommands.Apply(tab, new FailingFormatter(FormatFailure.TokenMismatch), new FormatterOptions(), null, out var message);

        Assert.False(changed);
        Assert.Equal("select 1 from dual", tab.Text);
        Assert.Contains("토큰", message);
        Assert.False(tab.Document.UndoStack.CanUndo);

        foreach (var f in new[] { FormatFailure.CommentCountMismatch, FormatFailure.NotIdempotent })
        {
            FormatterProfilesCommands.Apply(tab, new FailingFormatter(f), new FormatterOptions(), null, out var m);
            Assert.NotNull(m);
        }
    }

    [Fact]
    public void Read_only_documents_are_not_formatted()
    {
        var tab = Tab("select 1 from dual");
        tab.IsReadOnly = true;

        Assert.False(FormatterProfilesCommands.Apply(tab, RealFormatter(), new FormatterOptions(), null, out var message));
        Assert.NotNull(message);
        Assert.Equal("select 1 from dual", tab.Text);
    }

    [Fact]
    public async Task Commands_format_statement_at_caret_and_selection_or_document()
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select a,b from t;\nselect c,d from u;");
        host.Active = tab;
        var notices = new List<string>();
        var handler = new FormatterProfilesCommandHandler
        {
            Host = host,
            Formatter = RealFormatter(),
            OptionsProvider = _ => Task.FromResult(new FormatterOptions()),
            Notify = m => { notices.Add(m); return Task.CompletedTask; },
        };
        var keys = FakeContextKeys.AllTrue;

        tab.UpdateCaret(2, 2, 0); // 첫 문장
        await handler.ExecuteAsync(new CommandContext(FormatterProfilesCommands.FormatStatement, keys), default);
        Assert.StartsWith("SELECT", tab.Text);
        Assert.EndsWith("select c,d from u;", tab.Text);

        await handler.ExecuteAsync(new CommandContext(FormatterProfilesCommands.FormatDocument, keys), default);
        Assert.DoesNotContain("select c,d", tab.Text);
        Assert.Empty(notices);

        await handler.ExecuteAsync(new CommandContext(FormatterProfilesCommands.FormatDocument, keys), default);
        Assert.Single(notices); // 이미 포맷됨
    }

    [Fact]
    public async Task Failing_command_notifies_and_leaves_text()
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select 1 from dual");
        host.Active = tab;
        var notices = new List<string>();
        var handler = new FormatterProfilesCommandHandler
        {
            Host = host,
            Formatter = new FailingFormatter(FormatFailure.CommentCountMismatch),
            OptionsProvider = _ => Task.FromResult(new FormatterOptions()),
            Notify = m => { notices.Add(m); return Task.CompletedTask; },
        };

        await handler.ExecuteAsync(new CommandContext(FormatterProfilesCommands.FormatDocument, FakeContextKeys.AllTrue), default);

        Assert.Equal("select 1 from dual", tab.Text);
        Assert.Contains("주석", Assert.Single(notices));
    }

    [Fact]
    public async Task Profiles_command_opens_the_panel_and_loads()
    {
        var opened = new List<string>();
        var handler = new FormatterProfilesCommandHandler
        {
            OpenDocument = (id, vm, title) => { opened.Add(id); return vm; },
        };

        await handler.ExecuteAsync(new CommandContext(FormatterProfilesCommands.ShowProfiles, FakeContextKeys.AllTrue), default);

        Assert.Equal(["oranda.panel.formatterProfiles"], opened);
    }

    [Fact]
    public Task View_shows_options_and_preview_in_headless_window() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync();
        await vm.CopySelectedAsync();
        var view = new FormatterProfilesView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // TSK-13-42: 옵션은 묶음 판(OptionGroupsPanel)에 놓인다. 옵션 칸(Tag·OptionPaths = 경로)이 스키마의 모든 옵션을 덮는다.
        var panel = view.OptionsPanel!;
        var paths = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<Control>()
            .SelectMany(c => OptionPaths.Get(c) ?? (c.Classes.Contains("fmt-option") && c.Tag is string p ? [p] : []))
            .Distinct().ToList();
        Assert.Equal(FormatterOptionSchema.All.Count, paths.Count);
        Assert.Contains("SELECT", view.FindControl<Oranda.App.Features.SqlEditor.SqlCodeView>("PreviewBox")!.Text);

        vm.Rows.Single(r => r.Path == "case.keyword").ChoiceValue = nameof(LetterCase.Lower);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("select", view.FindControl<Oranda.App.Features.SqlEditor.SqlCodeView>("PreviewBox")!.Text);
        window.Close();
    });

    private sealed class FailingFormatter(FormatFailure failure) : ISqlFormatter
    {
        public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
            => new(text, new TextRange(0, text.Length), failure) { Message = "시험" };
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();

        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Current));

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : IFormatterProfileStore
    {
        private readonly List<FormatterProfile> _user = [];

        private static readonly FormatterProfile[] BuiltIns =
        [
            new() { Id = "oranda-default", Name = "Oranda 기본" },
            new() { Id = "orange5", Name = "Orange 5 스타일", Options = new FormatterOptions { CommaPosition = CommaPosition.Leading } },
            new() { Id = "compact", Name = "간결", Options = new FormatterOptions { Indent = new IndentOptions { Size = 2 } } },
        ];

        public IReadOnlyList<FormatterProfile> Items => [.. BuiltIns, .. _user];

        public Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct) => Task.FromResult(Items);

        public Task SaveAsync(FormatterProfile profile, CancellationToken ct)
        {
            _user.RemoveAll(p => p.Id == profile.Id);
            _user.Add(profile);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            _user.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }

        public Task ExportAsync(string id, string path, CancellationToken ct)
        {
            var p = Items.Single(x => x.Id == id);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(p, Oranda.Core.Serialization.OrandaJsonContext.Default.FormatterProfile));
            return Task.CompletedTask;
        }

        public Task<FormatterProfile> ImportAsync(string path, CancellationToken ct)
        {
            var p = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), Oranda.Core.Serialization.OrandaJsonContext.Default.FormatterProfile)!;
            var id = p.Id;
            for (var n = 2; Items.Any(x => x.Id == id); n++)
            {
                id = p.Id + "-" + n;
            }

            var name = p.Name;
            for (var n = 2; Items.Any(x => x.Name == name); n++)
            {
                name = $"{p.Name} ({n})";
            }

            var saved = p with { Id = id, Name = name };
            _user.Add(saved);
            return Task.FromResult(saved);
        }
    }
}
