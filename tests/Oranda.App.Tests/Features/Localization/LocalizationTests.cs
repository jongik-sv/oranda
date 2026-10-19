using System.Text.RegularExpressions;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Localization;

/// <summary>TSK-10-02 문자열 검사: 한/영 리소스의 키 집합·빈 값·번역 누락, 등록된 명령·도구 창·메뉴·코드가 쓰는 키 전부.</summary>
public partial class LocalizationTests
{
    private static readonly IReadOnlyDictionary<string, string> Ko = StringCatalog.Load("ko");
    private static readonly IReadOnlyDictionary<string, string> En = StringCatalog.Load("en");

    [GeneratedRegex("[\\uAC00-\\uD7A3]")]
    private static partial Regex Hangul();

    [GeneratedRegex("\\b(?:(?:Try)?Lookup|UiText\\.Get)\\(\"([^\"]+)\"")]
    private static partial Regex LookupCall();

    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.Localization", typeof(LocalizationCommands).Namespace);
        Assert.Equal(["oranda.tools.language"], LocalizationCommands.Ids);
    }

    // ---- 인수조건: 언어 전환 시 누락 문자열 0개(자동 검사) ----

    [Fact]
    public void Both_resource_files_are_embedded_and_not_empty()
    {
        Assert.Equal(["ko", "en"], StringCatalog.Languages);
        Assert.True(Ko.Count > 250);
        Assert.True(En.Count > 250);
        Assert.Empty(StringCatalog.Load("zz"));
        Assert.Null(StringCatalog.Get("zz", "Menu.file"));
    }

    [Fact]
    public void Korean_and_English_have_exactly_the_same_keys()
    {
        Assert.Empty(Ko.Keys.Except(En.Keys).Order());
        Assert.Empty(En.Keys.Except(Ko.Keys).Order());
    }

    [Fact]
    public void No_resource_value_is_blank()
    {
        foreach (var (lang, table) in new[] { ("ko", Ko), ("en", En) })
        {
            foreach (var (key, value) in table)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{lang}:{key} 가 비었다");
            }
        }
    }

    [Fact]
    public void English_names_contain_no_korean_except_language_names()
    {
        var offenders = En
            .Where(kv => !kv.Key.StartsWith("Shell.settings.language.", StringComparison.Ordinal))
            .Where(kv => Hangul().IsMatch(kv.Value))
            .Select(kv => kv.Key + "=" + kv.Value)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Korean_names_contain_korean_except_proper_names()
    {
        // 영어 그대로 두어도 되는 이름(제품·약어)만 예외다.
        string[] same = ["Menu.sql", "Menu.sql.access", "Cat.Sql", "Shell.window.title", "Shell.toolbar.more", "Shell.settings.language.en", "Shell.settings.dbmsOutput"];
        var offenders = Ko.Where(kv => !Hangul().IsMatch(kv.Value) && !same.Contains(kv.Key))
            .Select(kv => kv.Key + "=" + kv.Value)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_registered_command_has_korean_and_english_names()
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            var commands = services.GetRequiredService<ICommandRegistry>().Commands;
            Assert.True(commands.Count > 100);
            var ko = new ShellText(language: "ko");
            var en = new ShellText(language: "en");
            foreach (var c in commands)
            {
                Assert.True(Ko.ContainsKey("Cmd." + c.Id), "ko 이름 없음: " + c.Id);
                Assert.True(En.ContainsKey("Cmd." + c.Id), "en 이름 없음: " + c.Id);
                Assert.False(Hangul().IsMatch(en.Title(c)), $"영어 이름에 한글: {c.Id} → {en.Title(c)}");
                Assert.True(Hangul().IsMatch(ko.Title(c)), $"한국어 이름에 한글 없음: {c.Id} → {ko.Title(c)}");
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    [Fact]
    public void Every_registered_tool_panel_has_korean_and_english_titles()
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            var panels = services.GetRequiredService<IToolPanelRegistry>().Panels;
            Assert.True(panels.Count >= 20);
            foreach (var p in panels)
            {
                Assert.True(Ko.ContainsKey("Panel." + p.Id), "ko 제목 없음: " + p.Id);
                Assert.True(En.ContainsKey("Panel." + p.Id), "en 제목 없음: " + p.Id);
                Assert.NotEqual(Ko["Panel." + p.Id], En["Panel." + p.Id]);
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    [Fact]
    public void Every_key_the_menu_bar_and_toolbar_ask_for_exists_in_both_languages()
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            var commands = services.GetRequiredService<ICommandRegistry>().Commands;
            var panels = services.GetRequiredService<IToolPanelRegistry>().Panels;
            foreach (var lang in StringCatalog.Languages)
            {
                var rec = new RecordingText(new ShellText(language: lang));
                MenuModelBuilder.BuildMenuBar(commands, rec, _ => null, panels);
                MenuModelBuilder.BuildToolbar(commands, rec, _ => null);

                // 오른쪽 클릭 메뉴(context 배치)의 하위 메뉴 이름도 같다.
                var surfaces = commands.SelectMany(c => c.Placements)
                    .Where(p => p.StartsWith("context:", StringComparison.Ordinal))
                    .Select(p => p["context:".Length..].Split('/')[0])
                    .Distinct();
                foreach (var surface in surfaces)
                {
                    MenuModelBuilder.BuildContext(commands, rec, _ => null, surface);
                }


                // 최상위 메뉴 이름·하위 메뉴 이름·도구 창 목록 이름을 포함해 모두 리소스에 있어야 한다.
                var missing = rec.Asked.Where(k => StringCatalog.Get(lang, k) is null).Distinct().Order().ToList();
                Assert.Empty(missing);
                Assert.Contains("Menu.view.panels", rec.Asked);
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    [Fact]
    public void Every_top_menu_has_an_access_key_and_the_letters_are_unique_per_language()
    {
        foreach (var lang in StringCatalog.Languages)
        {
            var letters = new List<char>();
            foreach (var id in MenuModelBuilder.TopMenus)
            {
                var header = StringCatalog.Get(lang, $"Menu.{id}.access");
                Assert.NotNull(header);
                var at = header!.IndexOf('_', StringComparison.Ordinal);
                Assert.True(at >= 0 && at < header.Length - 1, $"{lang}:{id} 접근 키 표시(_) 없음: {header}");
                Assert.Equal(1, header.Count(c => c == '_'));
                letters.Add(char.ToUpperInvariant(header[at + 1]));
            }

            Assert.Equal(letters.Count, letters.Distinct().Count());
        }
    }

    [Fact]
    public void Every_category_has_a_name_in_both_languages()
    {
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            Assert.True(Ko.ContainsKey("Cat." + category), "ko " + category);
            Assert.True(En.ContainsKey("Cat." + category), "en " + category);
        }
    }

    [Fact]
    public void Every_key_literal_used_by_app_source_exists_in_both_languages_and_none_is_left_unused()
    {
        var root = FindRepoRoot();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var all = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Oranda.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            all.Append(text);
            foreach (Match m in LookupCall().Matches(text))
            {
                var key = m.Groups[1].Value;
                if ((key.StartsWith("Shell.", StringComparison.Ordinal) || key.StartsWith("Menu.", StringComparison.Ordinal)) && !key.EndsWith('.'))
                {
                    used.Add(key);
                }
            }
        }

        Assert.True(used.Count >= 20, "코드의 키 검색이 동작하지 않는다");
        Assert.Empty(used.Where(k => !Ko.ContainsKey(k)));
        Assert.Empty(used.Where(k => !En.ContainsKey(k)));

        // 안 쓰는 Shell.* 글자를 쌓아 두지 않는다.
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(root, "src", "Oranda.App"), "*.axaml", SearchOption.AllDirectories))
        {
            all.Append(File.ReadAllText(xaml));
        }

        var source = all.ToString();
        var unused = Ko.Keys.Where(k => k.StartsWith("Shell.", StringComparison.Ordinal))
            .Where(k => !source.Contains('"' + k + '"', StringComparison.Ordinal))
            // 설정 창 글자는 XAML 에서 {Binding Strings[키]} 로 부른다.
            .Where(k => !k.StartsWith("Shell.settings.", StringComparison.Ordinal) || !source.Contains("Strings[" + k["Shell.settings.".Length..] + "]", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(unused);
    }

    [GeneratedRegex("(\\S+)=\"([^\"{][^\"]*)\"")]
    private static partial Regex XamlAttribute();

    [GeneratedRegex(">([^<>{]*[\\uAC00-\\uD7A3][^<>]*)<")]
    private static partial Regex XamlInnerText();

    [Fact]
    public void Every_korean_literal_in_xaml_has_an_english_counterpart_on_a_property_the_translator_handles()
    {
        var root = FindRepoRoot();
        string[] handled = ["Title", "Text", "Content", "Header", "PlaceholderText", "AutomationProperties.Name", "ToolTip.Tip", "FormatString"];
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var unhandled = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Oranda.App"), "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in XamlAttribute().Matches(text))
            {
                var value = m.Groups[2].Value;
                if (!Hangul().IsMatch(value))
                {
                    continue;
                }

                count++;
                if (!handled.Contains(m.Groups[1].Value))
                {
                    unhandled.Add(Path.GetFileName(file) + ":" + m.Groups[1].Value + "=" + value);
                }
                else if (!LiteralTranslator.Table.ContainsKey(value))
                {
                    missing.Add(Path.GetFileName(file) + ": " + value);
                }
            }

            foreach (Match m in XamlInnerText().Matches(text))
            {
                unhandled.Add(Path.GetFileName(file) + ": (inner text) " + m.Groups[1].Value.Trim());
            }
        }

        Assert.True(count > 200, "XAML 글자 검색이 동작하지 않는다");
        Assert.Empty(missing);
        Assert.Empty(unhandled);
    }

    [Fact]
    public void The_literal_table_maps_korean_to_english_without_leftovers()
    {
        Assert.True(LiteralTranslator.Table.Count > 200);
        foreach (var (ko, en) in LiteralTranslator.Table)
        {
            Assert.True(Hangul().IsMatch(ko), "한국어 키가 아니다: " + ko);
            Assert.False(Hangul().IsMatch(en), "영어 값에 한글: " + ko + " → " + en);
            Assert.False(string.IsNullOrWhiteSpace(en), "빈 값: " + ko);
        }
    }

    // ---- ShellText: 언어 선택·바꿈·되돌림 ----

    [Fact]
    public void Titles_follow_the_selected_language_and_switch_back()
    {
        var text = new ShellText();
        var save = new CommandDefinition { Id = "oranda.file.save", Title = "Cmd.oranda.file.save", Category = CommandCategory.File };
        Assert.Equal("ko", text.Language);
        Assert.Equal("저장", text.Title(save));
        Assert.Equal("파일", text.CategoryName(CommandCategory.File));

        text.SetLanguage("en");
        Assert.Equal("en", text.Language);
        Assert.Equal("Save", text.Title(save));
        Assert.Equal("File", text.CategoryName(CommandCategory.File));
        Assert.Equal("Command Palette", text.Lookup("Shell.palette.automation"));

        text.SetLanguage("ko");
        Assert.Equal("저장", text.Title(save));
    }

    [Fact]
    public void LanguageChanged_is_raised_only_when_the_language_really_changes()
    {
        var text = new ShellText();
        var count = 0;
        text.LanguageChanged += (_, _) => count++;

        text.SetLanguage("ko");
        Assert.Equal(0, count);
        text.SetLanguage("en");
        Assert.Equal(1, count);
        text.SetLanguage("en");
        Assert.Equal(1, count);
        text.SetLanguage("ko");
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("system")]
    [InlineData("EN")]
    public void Unknown_language_codes_fall_back_to_korean_without_throwing(string code)
    {
        var text = new ShellText();
        text.SetLanguage(code);
        Assert.Equal("ko", text.Language);
        Assert.Equal("저장", text.Lookup("Shell.settings.save"));
    }

    [Fact]
    public void Commands_whose_attribute_carries_a_literal_korean_title_still_change_language()
    {
        // oranda.grid.export 는 특성에 「내보내기」를 직접 적었다(다른 Task 파일). ID 로 만든 키가 먼저 쓰인다.
        var export = new CommandDefinition { Id = "oranda.grid.export", Title = "내보내기", Category = CommandCategory.Result };
        var text = new ShellText(language: "en");
        Assert.Equal("Export", text.Title(export));
        text.SetLanguage("ko");
        Assert.Equal("내보내기", text.Title(export));
    }

    [Fact]
    public void Keys_missing_from_the_resources_get_a_readable_fallback_and_never_throw()
    {
        var text = new ShellText(language: "en");
        var ext = new CommandDefinition { Id = "oranda.ext.aiChat.explainPlan", Title = "Cmd.oranda.ext.aiChat.explainPlan", Category = CommandCategory.Tools };
        Assert.Equal("aiChat explain plan", text.Title(ext));
        Assert.Null(text.TryLookup("Shell.no.such.key"));
        Assert.Equal("such key", text.Lookup("Shell.no.such.key"));
        Assert.Throws<ArgumentNullException>(() => text.Lookup(null!));
        Assert.Throws<ArgumentNullException>(() => text.Title((CommandDefinition)null!));
    }

    [Fact]
    public void An_extension_resource_lookup_wins_over_the_built_in_table()
    {
        var text = new ShellText(k => k == "Shell.settings.save" ? "Store" : null, "en");
        Assert.Equal("Store", text.Lookup("Shell.settings.save"));
        Assert.Equal("Close", text.Lookup("Shell.settings.close"));
    }

    [Fact]
    public void LookupIn_reads_another_language_without_changing_the_current_one()
    {
        var text = new ShellText(language: "en");
        Assert.Equal("저장", text.LookupIn("ko", "Shell.settings.save"));
        Assert.Equal("Save", text.Lookup("Shell.settings.save"));
        Assert.Equal("en", text.Language);
    }

    // ---- 설정 값 → 실제 언어 ----

    [Theory]
    [InlineData("system", "ko", "ko")]
    [InlineData("system", "en", "en")]
    [InlineData("system", "ja", "en")]
    [InlineData("system", "", "en")]
    [InlineData("system", null, "en")]
    [InlineData("system", " KO ", "ko")]
    [InlineData("ko", "en", "ko")]
    [InlineData("en", "ko", "en")]
    [InlineData("EN", "ko", "en")]
    [InlineData(" ko ", "en", "ko")]
    [InlineData("", "ko", "ko")]
    [InlineData(null, "en", "en")]
    [InlineData("fr", "ko", "ko")]
    [InlineData("fr", "de", "en")]
    public void Resolve_picks_the_language_from_the_setting_or_the_os(string? setting, string? os, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(setting, os));
    }

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("En", "en")]
    [InlineData("", "system")]
    [InlineData(null, "system")]
    [InlineData("zz", "system")]
    public void Normalize_keeps_only_system_ko_en(string? setting, string expected)
    {
        Assert.Equal(expected, UiLanguage.Normalize(setting));
    }

    // ---- 글자 배율 ----

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.8, 0.8)]
    [InlineData(2.5, 2.5)]
    [InlineData(0.1, 0.8)]
    [InlineData(0.0, 0.8)]
    [InlineData(-3.0, 0.8)]
    [InlineData(9.0, 2.5)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void Ui_scale_is_clamped_to_0_8_through_2_5(double input, double expected)
    {
        Assert.Equal(expected, ThemeService.ClampScale(input));
    }

    [Fact]
    public void UiText_falls_back_for_unknown_keys_and_rejects_a_null_key()
    {
        // UiText.Current 는 앱당 하나인 전역이라 시험끼리 겹치지 않게 값을 바꾸지 않고 읽기만 한다.
        Assert.Equal("기본", UiText.Get("Shell.dialog.no.such", "기본"));
        Assert.Throws<ArgumentNullException>(() => UiText.Get(null!, "x"));
    }

    [Fact]
    public async Task Language_command_is_registered_in_the_tools_menu_and_opens_the_settings_window()
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            var registry = services.GetRequiredService<ICommandRegistry>();
            var def = registry.Commands.Single(c => c.Id == "oranda.tools.language");
            Assert.Equal(CommandCategory.Tools, def.Category);
            Assert.Contains("menu:tools/language@12", def.Placements);

            var ui = new RecordingUi();
            var handler = new LanguageCommandHandler(ui);
            await handler.ExecuteAsync(new CommandContext("oranda.tools.language", services.GetRequiredService<IContextKeys>()), CancellationToken.None);
            Assert.Equal(1, ui.SettingsShown);
        }
        finally
        {
            services.Dispose();
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.GetFiles(dir.FullName, "Oranda.sln*").Length > 0)
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }

    /// <summary>메뉴·툴바가 요청한 키를 기록하는 얇은 감싸개.</summary>
    private sealed class RecordingText(IShellText inner) : IShellText
    {
        public List<string> Asked { get; } = [];

        public string Language => inner.Language;

        public event EventHandler? LanguageChanged
        {
            add { }
            remove { }
        }

        public void SetLanguage(string language) => inner.SetLanguage(language);

        public string Lookup(string key)
        {
            Asked.Add(key);
            return inner.Lookup(key);
        }

        public string? TryLookup(string key) => inner.TryLookup(key);

        public string LookupIn(string language, string key) => inner.LookupIn(language, key);

        public string Title(CommandDefinition command)
        {
            Asked.Add("Cmd." + command.Id);
            return inner.Title(command);
        }

        public string Title(ToolPanelDefinition panel)
        {
            Asked.Add("Panel." + panel.Id);
            return inner.Title(panel);
        }

        public string CategoryName(CommandCategory category) => inner.CategoryName(category);
    }

    private sealed class RecordingUi : IShellUi
    {
        public int SettingsShown { get; private set; }

        public Oranda.App.Shell.Docking.DockWorkspace Docking => throw new NotSupportedException();

        public Oranda.App.Shell.Settings.SettingsService Settings => throw new NotSupportedException();

        public Oranda.Core.Connection.ConnectionEnvironment? ActiveEnvironment { get; set; }

        public void ShowCommandPalette() => throw new NotSupportedException();

        public void ShowSettings() => SettingsShown++;

        public void ShowAbout() => throw new NotSupportedException();

        public void CloseMainWindow() => throw new NotSupportedException();

        public void FocusDocument() => throw new NotSupportedException();
    }
}
