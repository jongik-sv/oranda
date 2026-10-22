using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Shell;

/// <summary>TSK-13-38 설정 창 「SQL 포맷」 쪽: 프로필 읽기·저장·다시 열기·기본값·닫기 되돌림·기본 제공 프로필 복사·미리보기·창 단추.</summary>
[Collection("UiLanguage")]
public sealed class SqlFormatSettingsTests
{
    private static ISqlFormatter RealFormatter()
    {
        var type = typeof(Oranda.Language.LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static readonly ShellText Ko = new(language: "ko");

    private static async Task<(SettingsService Settings, MemorySettingsStore SettingsStore, ProfileStore Profiles, SettingsViewModel Vm)> Open(
        ProfileStore? profiles = null, string? profileId = null, SettingsService? settings = null, MemorySettingsStore? settingsStore = null)
    {
        settingsStore ??= new MemorySettingsStore();
        settings ??= new SettingsService(settingsStore);
        if (profileId is not null)
        {
            await settings.UpdateAsync(s => s with { FormatterProfileId = profileId });
        }

        profiles ??= new ProfileStore();
        var page = new SqlFormatPage(profiles, RealFormatter(), settings.Current.FormatterProfileId, Ko);
        await page.Loaded;
        return (settings, settingsStore, profiles, new SettingsViewModel(settings, Ko, sqlFormat: page));
    }

    [Fact]
    public async Task Reads_the_four_values_from_the_default_profile()
    {
        var (_, _, _, vm) = await Open(profileId: FormatterIds.Orange5);
        var f = vm.SqlFormat;

        Assert.Equal(FormatterIds.Orange5, f.ProfileId);
        Assert.True(f.CommaLeading);
        Assert.False(f.CommaTrailing);
        Assert.True(f.AlignLeft);
        Assert.True(f.CaseUpper);
        Assert.True(f.ByListSingle);
        Assert.Contains("Orange 5 스타일", f.ProfileText, StringComparison.Ordinal);
        Assert.Contains("기본 제공", f.ProfileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_built_in_profile_saves_a_user_copy_and_switches_to_it_and_reopening_keeps_the_values()
    {
        var (settings, store, profiles, vm) = await Open();
        vm.SqlFormat.AlignRight = true;
        vm.SqlFormat.CommaLeading = true;
        vm.SqlFormat.CaseLower = true;
        vm.SqlFormat.ByListMulti = true;

        await vm.SaveAsync();

        var copy = profiles.Items.Single(p => p.Id == FormatterIds.SettingsCopy);
        Assert.Equal("SQL 포맷(설정 창)", copy.Name);
        Assert.Equal(KeywordAlign.Right, copy.Options.KeywordAlign);
        Assert.Equal(CommaPosition.Leading, copy.Options.CommaPosition);
        Assert.Equal(LetterCase.Lower, copy.Options.Case.Keyword);
        Assert.Equal(LetterCase.Upper, copy.Options.Case.Function); // 함수 대소문자는 「자세히…」 몫
        Assert.Equal(ByListLayout.MultiLine, copy.Options.LineBreaks.GroupOrderBy);
        Assert.Equal(FormatterIds.SettingsCopy, store.Saved.FormatterProfileId);

        var (_, _, _, again) = await Open(profiles, settings: settings, settingsStore: store);
        Assert.Equal(FormatterIds.SettingsCopy, again.SqlFormat.ProfileId);
        Assert.True(again.SqlFormat.AlignRight);
        Assert.True(again.SqlFormat.CommaLeading);
        Assert.True(again.SqlFormat.CaseLower);
        Assert.True(again.SqlFormat.ByListMulti);
        Assert.DoesNotContain("기본 제공", again.SqlFormat.ProfileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_copy_from_an_earlier_save_is_not_overwritten()
    {
        var profiles = new ProfileStore();
        var tuned = new FormatterProfile { Id = FormatterIds.SettingsCopy, Name = "SQL 포맷(설정 창)", Options = new FormatterOptions { Indent = new IndentOptions { Size = 3 } } };
        await profiles.SaveAsync(tuned, default);
        var (_, store, _, vm) = await Open(profiles, profileId: FormatterIds.Orange5);
        vm.SqlFormat.AlignRight = true;

        await vm.SaveAsync();

        Assert.Equal(tuned, profiles.Items.Single(p => p.Id == FormatterIds.SettingsCopy));
        var second = profiles.Items.Single(p => p.Id == FormatterIds.SettingsCopy + "-2");
        Assert.Equal("SQL 포맷(설정 창) 2", second.Name);
        Assert.Equal(KeywordAlign.Right, second.Options.KeywordAlign);
        Assert.Equal(CommaPosition.Leading, second.Options.CommaPosition); // Orange5 바탕
        Assert.Equal(second.Id, store.Saved.FormatterProfileId);
    }

    [Fact]
    public async Task A_choice_made_while_the_profile_is_still_loading_is_kept()
    {
        var profiles = new ProfileStore { ListGate = new TaskCompletionSource() };
        var page = new SqlFormatPage(profiles, RealFormatter(), FormatterIds.Orange5, Ko);
        page.AlignRight = true;
        profiles.ListGate.SetResult();
        await page.Loaded;

        Assert.True(page.AlignRight);
        Assert.Equal(FormatterIds.Orange5, page.ProfileId);
        Assert.Equal(KeywordAlign.Right, (await page.SaveAsync() is { } id ? profiles.Items.Single(p => p.Id == id) : null)!.Options.KeywordAlign);
    }

    [Fact]
    public async Task When_settings_fail_after_the_copy_a_second_save_still_switches_to_the_copy()
    {
        var failing = new FlakySettingsStore { FailNext = true };
        var settings = new SettingsService(failing);
        var profiles = new ProfileStore();
        var page = new SqlFormatPage(profiles, RealFormatter(), settings.Current.FormatterProfileId, Ko);
        await page.Loaded;
        var vm = new SettingsViewModel(settings, Ko, sqlFormat: page);
        vm.SqlFormat.CommaLeading = true;

        await vm.SaveAsync();
        Assert.Equal("쓰기 실패", vm.SqlFormat.ErrorText);
        Assert.Equal(SettingsViewModel.FormatTabIndex, vm.SelectedTab);

        await vm.SaveAsync();
        Assert.Equal(string.Empty, vm.SqlFormat.ErrorText);
        Assert.Equal(FormatterIds.SettingsCopy, failing.Saved.FormatterProfileId);
        Assert.Single(profiles.Items, p => p.Id.StartsWith(FormatterIds.SettingsCopy, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_profile_is_written_in_place_keeping_its_other_options()
    {
        var profiles = new ProfileStore();
        await profiles.SaveAsync(new FormatterProfile { Id = "team", Name = "팀", Options = new FormatterOptions { Indent = new IndentOptions { Size = 2 } } }, default);
        var (_, store, _, vm) = await Open(profiles, profileId: "team");
        vm.SqlFormat.CommaLeading = true;

        await vm.SaveAsync();

        var team = profiles.Items.Single(p => p.Id == "team");
        Assert.Equal(CommaPosition.Leading, team.Options.CommaPosition);
        Assert.Equal(2, team.Options.Indent.Size);
        Assert.Equal("team", store.Saved.FormatterProfileId);
        Assert.DoesNotContain(profiles.Items, p => p.Id == FormatterIds.SettingsCopy);
    }

    [Fact]
    public async Task Saving_without_changes_writes_no_profile()
    {
        var (_, store, profiles, vm) = await Open();
        await vm.SaveAsync();

        Assert.Equal(0, profiles.SaveCount);
        Assert.Equal(FormatterIds.Default, store.Saved.FormatterProfileId);
    }

    [Fact]
    public async Task Close_discards_the_changes()
    {
        var (settings, store, profiles, vm) = await Open();
        vm.SqlFormat.CommaLeading = true;
        vm.SqlFormat.AlignRight = true;
        vm.Close();

        Assert.Equal(0, profiles.SaveCount);
        var (_, _, _, again) = await Open(profiles, settings: settings, settingsStore: store);
        Assert.True(again.SqlFormat.CommaTrailing);
        Assert.True(again.SqlFormat.AlignLeft);
    }

    [Fact]
    public async Task Default_button_resets_only_the_four_values()
    {
        var profiles = new ProfileStore();
        await profiles.SaveAsync(new FormatterProfile
        {
            Id = "team",
            Name = "팀",
            Options = new FormatterOptions
            {
                KeywordAlign = KeywordAlign.Right,
                CommaPosition = CommaPosition.Leading,
                Case = new CaseOptions { Keyword = LetterCase.Lower, Function = LetterCase.Lower },
                Indent = new IndentOptions { Size = 2 },
                LineBreaks = new LineBreakOptions { GroupOrderBy = ByListLayout.MultiLine, MaxLineWidth = 80 },
            },
        }, default);
        var (_, _, _, vm) = await Open(profiles, profileId: "team");

        // TSK-13-42: 「기본값」 = 기본 제공 「Oranda 기본」(FormatterOptions.OrandaDefault, 사내 서식)의 4항목.
        vm.SqlFormat.ResetDefaultsCommand.Execute(null);
        Assert.Equal(FormatterOptions.OrandaDefault.KeywordAlign == KeywordAlign.Left, vm.SqlFormat.AlignLeft);
        Assert.True(vm.SqlFormat.CommaLeading);
        Assert.True(vm.SqlFormat.CaseUpper);
        Assert.True(vm.SqlFormat.ByListMulti);
        await vm.SaveAsync();

        var o = profiles.Items.Single(p => p.Id == "team").Options;
        Assert.Equal(FormatterOptions.OrandaDefault.KeywordAlign, o.KeywordAlign);
        Assert.Equal(FormatterOptions.OrandaDefault.CommaPosition, o.CommaPosition);
        Assert.Equal(FormatterOptions.OrandaDefault.Case.Keyword, o.Case.Keyword);
        Assert.Equal(LetterCase.Lower, o.Case.Function);
        Assert.Equal(FormatterOptions.OrandaDefault.LineBreaks.GroupOrderBy, o.LineBreaks.GroupOrderBy);
        Assert.Equal(2, o.Indent.Size);
        Assert.Equal(80, o.LineBreaks.MaxLineWidth);
    }

    [Fact]
    public async Task A_radio_turning_off_does_not_change_the_value()
    {
        var (_, _, _, vm) = await Open();
        vm.SqlFormat.CommaLeading = true;
        vm.SqlFormat.CommaTrailing = false; // 같은 묶음의 다른 단추가 꺼질 때 오는 값
        Assert.Equal(CommaPosition.Leading, vm.SqlFormat.Comma);
        vm.SqlFormat.CaseAsIs = true;
        vm.SqlFormat.CaseUpper = false;
        Assert.Equal(LetterCase.AsIs, vm.SqlFormat.KeywordCase);
    }

    [Fact]
    public async Task Preview_reformats_on_every_change()
    {
        var (_, _, _, vm) = await Open();
        var changed = new List<string?>();
        vm.SqlFormat.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.StartsWith("SELECT e.id,", vm.SqlFormat.Preview, StringComparison.Ordinal);
        Assert.Contains("\nFROM emp e", vm.SqlFormat.Preview, StringComparison.Ordinal);
        vm.SqlFormat.AlignRight = true;
        Assert.Contains("\n  FROM emp e", vm.SqlFormat.Preview, StringComparison.Ordinal);
        vm.SqlFormat.CommaLeading = true;
        Assert.Contains("\n     , e.name", vm.SqlFormat.Preview, StringComparison.Ordinal);
        vm.SqlFormat.CaseLower = true;
        Assert.StartsWith("select", vm.SqlFormat.Preview, StringComparison.Ordinal);
        vm.SqlFormat.ByListMulti = true;
        Assert.Contains(" group by e.id\n", vm.SqlFormat.Preview, StringComparison.Ordinal);
        Assert.Equal(4, changed.Count(n => n == nameof(SqlFormatPage.Preview)));
    }

    [Fact]
    public async Task A_failed_profile_save_keeps_the_window_open_on_the_format_tab_and_saves_nothing()
    {
        var profiles = new ProfileStore { FailSave = true };
        var (_, store, _, vm) = await Open(profiles);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.SqlFormat.CommaLeading = true;
        vm.TabSize = 7;

        await vm.SaveAsync();

        Assert.False(closed);
        Assert.Equal(SettingsViewModel.FormatTabIndex, vm.SelectedTab);
        Assert.Equal("디스크가 가득 찼다", vm.SqlFormat.ErrorText);
        Assert.NotEqual(7, store.Saved.Editor.TabSize);
    }

    [Fact]
    public async Task Without_a_profile_store_the_page_shows_defaults_and_saves_nothing()
    {
        var page = new SqlFormatPage(null, null, FormatterIds.Default, Ko);
        await page.Loaded;

        Assert.False(page.CanSave);
        Assert.False(page.HasPreview);
        Assert.Equal(string.Empty, page.Preview);
        Assert.True(page.CommaLeading); // 프로필이 없으면 「Oranda 기본」(사내 서식) 값을 보인다
        Assert.False(page.KeywordAlignApplies); // 사내 서식 = 열 맞춤
        page.CommaTrailing = true;
        Assert.Null(await page.SaveAsync());
    }

    [Fact]
    public async Task An_unknown_profile_id_falls_back_to_the_first_profile()
    {
        var (_, _, _, vm) = await Open(profileId: "gone");
        Assert.Equal(FormatterIds.Default, vm.SqlFormat.ProfileId);
    }

    [Fact]
    public async Task More_saves_then_opens_the_profiles_screen()
    {
        var opened = 0;
        var (settings, store, profiles, _) = await Open();
        var page = new SqlFormatPage(profiles, RealFormatter(), settings.Current.FormatterProfileId, Ko);
        var vm = new SettingsViewModel(settings, Ko, sqlFormat: page) { OpenProfiles = () => { opened++; return Task.CompletedTask; } };
        vm.SqlFormat.CommaLeading = true;

        await vm.OpenFormatterProfilesCommand.ExecuteAsync(null);

        Assert.Equal(1, opened);
        Assert.Equal(FormatterIds.SettingsCopy, store.Saved.FormatterProfileId);
    }

    [Fact]
    public async Task Active_options_follow_the_settings_profile_id()
    {
        var (_, store, profiles, vm) = await Open();
        vm.SqlFormat.CommaLeading = true;
        await vm.SaveAsync();

        var o = await FormatterIds.ActiveOptionsAsync(store, profiles, default);
        Assert.Equal(CommaPosition.Leading, o.CommaPosition);
        Assert.Equal(FormatterOptions.OrandaDefault, await FormatterIds.ActiveOptionsAsync(null, null, default));
    }

    [Fact]
    public Task Settings_window_has_the_sql_format_tab_with_radio_groups() => HeadlessHost.Run(async () =>
    {
        var (_, _, _, vm) = await Open();
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var items = tabs.Items.OfType<TabItem>().ToList();
        Assert.Equal("SQL 포맷", (string?)items[SettingsViewModel.FormatTabIndex].Header);
        Assert.Equal("업데이트", (string?)items[SettingsViewModel.UpdateTabIndex].Header);
        tabs.SelectedIndex = SettingsViewModel.FormatTabIndex;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        Assert.True(Named<RadioButton>("FmtAlignLeft").IsChecked);
        Assert.True(Named<RadioButton>("FmtCommaTrailing").IsChecked);
        Assert.True(Named<RadioButton>("FmtCaseUpper").IsChecked);
        Assert.True(Named<RadioButton>("FmtBySingle").IsChecked);
        Assert.Equal("오른쪽", (string?)Named<RadioButton>("FmtAlignRight").Content);
        Assert.Equal("기본값", (string?)Named<Button>("FmtDefaultButton").Content);
        Assert.Equal("자세히…", (string?)Named<Button>("FmtMoreButton").Content);

        Named<RadioButton>("FmtCommaLeading").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(CommaPosition.Leading, vm.SqlFormat.Comma);
        Assert.False(Named<RadioButton>("FmtCommaTrailing").IsChecked);
        Assert.Contains("\n    , e.name", Named<TextBlock>("FmtPreview").Text, StringComparison.Ordinal);

        Named<RadioButton>("FmtCommaTrailing").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Named<Button>("FmtDefaultButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<RadioButton>("FmtCommaLeading").IsChecked); // 「Oranda 기본」 = 앞 쉼표
        Assert.True(Named<RadioButton>("FmtByMulti").IsChecked);
        Assert.True(Named<Border>("FmtAlignGroup").IsEnabled); // 시험 저장소의 Oranda 기본 = 들여쓰기 배치
        window.Close();
    });

    [Fact]
    public Task Keyword_align_is_greyed_with_a_reason_when_the_profile_uses_the_column_layout() => HeadlessHost.Run(async () =>
    {
        var profiles = new ProfileStore();
        await profiles.SaveAsync(new FormatterProfile { Id = "house", Name = "사내", Options = FormatterOptions.OrandaDefault }, default);
        var (_, _, _, vm) = await Open(profiles, profileId: "house");
        Assert.False(vm.SqlFormat.KeywordAlignApplies);
        Assert.Equal("사내 서식(열 맞춤)에서는 쓰지 않습니다", vm.SqlFormat.KeywordAlignTip);

        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = SettingsViewModel.FormatTabIndex;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var group = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "FmtAlignGroup");
        Assert.False(group.IsEnabled);
        Assert.False(window.GetVisualDescendants().OfType<RadioButton>().First(r => r.Name == "FmtAlignRight").IsEffectivelyEnabled);
        Assert.Equal("사내 서식(열 맞춤)에서는 쓰지 않습니다", ToolTip.GetTip(group));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "GROUP BY 열 목록");
        window.Close();

        var (_, _, _, indent) = await Open(profileId: FormatterIds.Orange5);
        Assert.True(indent.SqlFormat.KeywordAlignApplies);
        Assert.Null(indent.SqlFormat.KeywordAlignTip);
    });

    /// <summary>다음 저장 한 번을 실패시키는 설정 저장소.</summary>
    internal sealed class FlakySettingsStore : ISettingsStore
    {
        public Oranda.Core.Settings.AppSettings Saved { get; private set; } = new();

        public bool FailNext { get; set; }

        public Task<StoredDocument<Oranda.Core.Settings.AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<Oranda.Core.Settings.AppSettings>(Saved));

        public Task SaveAsync(Oranda.Core.Settings.AppSettings settings, CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("쓰기 실패");
            }

            Saved = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>기본 제공 3종 + 사용자 프로필을 메모리에 두는 저장소. 기본 제공은 고칠 수 없다(Platform 저장소와 같은 규칙).</summary>
    internal sealed class ProfileStore : IFormatterProfileStore
    {
        private readonly List<FormatterProfile> _user = [];

        private static readonly FormatterProfile[] BuiltIns =
        [
            new() { Id = FormatterIds.Default, Name = "Oranda 기본" },
            new() { Id = FormatterIds.Orange5, Name = "Orange 5 스타일", Options = new FormatterOptions { CommaPosition = CommaPosition.Leading } },
            new() { Id = FormatterIds.Compact, Name = "간결" },
        ];

        public IReadOnlyList<FormatterProfile> Items => [.. BuiltIns, .. _user];

        public int SaveCount { get; private set; }

        public bool FailSave { get; init; }

        public TaskCompletionSource? ListGate { get; init; }

        public async Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct)
        {
            if (ListGate is { } gate)
            {
                await gate.Task;
            }

            return Items;
        }

        public Task SaveAsync(FormatterProfile profile, CancellationToken ct)
        {
            if (FailSave)
            {
                throw new IOException("디스크가 가득 찼다");
            }

            if (FormatterIds.IsBuiltIn(profile.Id))
            {
                throw new InvalidOperationException("기본 제공 프로필은 고칠 수 없다.");
            }

            SaveCount++;
            _user.RemoveAll(p => p.Id == profile.Id);
            _user.Add(profile);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;

        public Task ExportAsync(string id, string path, CancellationToken ct) => Task.CompletedTask;

        public Task<FormatterProfile> ImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
