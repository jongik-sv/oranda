using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.FormatterProfiles;

/// <summary>
/// TSK-13-42 포매터 프로필 화면(A안): 기본 제공 읽기 전용·복사해서 고치기·바꾼 점(파랑 = 기본과 다름, 주황 = 저장 안 함)·
/// 저장·되돌리기·새로·목록 묶음·오른쪽 클릭 메뉴·좁은 창 배치(3·2·1단)·라벨 잘림 없음·한국어 값.
/// </summary>
public class FormatterProfilesScreenTests
{
    private static readonly string[] EnumNames =
        ["Upper", "Lower", "AsIs", "Leading", "Trailing", "OnePerLine", "FillToWidth", "Left", "Right", "SingleLine", "MultiLine", "Before", "After", "None", "Keep", "Add", "Remove", "Indent", "Column", "FollowGroupBy"];

    private static readonly string[] KoreanValues = ["대문자", "소문자", "그대로", "뒤에", "앞에", "한 줄에 하나", "너비까지 채움", "앞에서", "안 함", "붙임", "= GROUP BY", "들여쓰기", "열 맞춤(하우스)"];

    private static readonly string[][][] Plans =
    [
        [["대소문자", "줄바꿈", "쉼표·맞춤", "들여쓰기", "정렬·공백", "주석·기타"]],
        [["대소문자", "들여쓰기", "정렬·공백"], ["줄바꿈", "쉼표·맞춤", "주석·기타"]],
        [["대소문자", "들여쓰기"], ["줄바꿈", "쉼표·맞춤"], ["정렬·공백", "주석·기타"]],
    ];

    private static readonly string[] OrderByLabels = ["= GROUP BY", "한 줄", "여러 줄"];

    private static readonly string[] MenuHeaders = ["복사해서 새 프로필", "이름 바꾸기", "삭제", "기본으로 지정", "내보내기…"];

    private static ISqlFormatter RealFormatter()
    {
        var type = typeof(Oranda.Language.LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static async Task<(FormatterProfilesViewModel Vm, MemoryStore Store)> LoadedAsync(string? select = null, params FormatterProfile[] mine)
    {
        var store = new MemoryStore(mine);
        var vm = new FormatterProfilesViewModel(store, RealFormatter(), new MemorySettings());
        await vm.LoadAsync();
        if (select is not null)
        {
            vm.Selected = vm.Profiles.Single(p => p.Profile.Id == select);
        }

        return (vm, store);
    }

    private static OptionRow Row(FormatterProfilesViewModel vm, string path) => vm.Rows.Single(r => r.Path == path);

    private static FormatterProfile Team => new()
    {
        Id = "team",
        Name = "우리 팀",
        Options = new FormatterOptions { CommaPosition = CommaPosition.Leading, Indent = new IndentOptions { Size = 2 } },
    };

    private static (Window Window, FormatterProfilesView View) Show(FormatterProfilesViewModel vm, double width = 1200, double height = 780)
    {
        var view = new FormatterProfilesView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump(window);
        return (window, view);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static IEnumerable<string> PathsOf(Control c)
        => OptionPaths.Get(c) ?? (c.Classes.Contains("fmt-option") && c.Tag is string p ? [p] : []);

    private static T OptionControl<T>(FormatterProfilesView view, string path, Func<T, bool>? pick = null)
        where T : Control
        => view.OptionsPanel!.GetVisualDescendants().OfType<Control>().Where(c => PathsOf(c).Contains(path))
            .SelectMany(c => c is T self ? [self] : c.GetVisualDescendants().OfType<T>())
            .First(t => pick?.Invoke(t) ?? true);

    private static Panel MarkerOf(FormatterProfilesView view, string path)
        => view.OptionsPanel!.GetVisualDescendants().OfType<Panel>().Single(p => p.Tag as string == FormatterOptionGroups.MarkerTag + path);

    [Fact]
    public async Task Rows_are_made_once_and_only_their_values_change_with_the_profile()
    {
        var (vm, _) = await LoadedAsync(null, Team);
        var rows = vm.Rows.ToList();
        Assert.Equal(FormatterOptionSchema.All.Count, rows.Count);
        Assert.Equal(4, (int)Row(vm, "indent.size").NumberValue);

        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");

        Assert.Equal(rows, vm.Rows); // 같은 행 객체
        Assert.Equal(2, (int)Row(vm, "indent.size").NumberValue);
        Assert.All(vm.Rows, r => Assert.True(r.IsEditable));
    }

    [Fact]
    public async Task Built_in_rows_refuse_changes_and_keep_preview_and_dirty_state()
    {
        var (vm, _) = await LoadedAsync("orange5");
        var before = vm.PreviewText;

        Row(vm, "case.keyword").ChoiceValue = nameof(LetterCase.Lower);
        Row(vm, "indent.size").NumberValue = 9;
        Row(vm, "align.equals").BoolValue = true;
        Row(vm, "case.keyword").ChoiceItems.Single(c => c.Name == nameof(LetterCase.AsIs)).IsChecked = true;

        Assert.Equal(nameof(LetterCase.Upper), Row(vm, "case.keyword").ChoiceValue);
        Assert.Equal(4, (int)Row(vm, "indent.size").NumberValue);
        Assert.False(Row(vm, "align.equals").BoolValue);
        Assert.Equal(before, vm.PreviewText);
        Assert.False(vm.IsDirty);
        Assert.Equal(0, vm.DirtyCount);
        Assert.False(vm.CanSave || vm.CanRevert || vm.CanRename || vm.CanDelete);
        Assert.True(vm.CanCopy);
        Assert.True(vm.IsBuiltInSelected);
    }

    [Fact]
    public async Task Blue_marks_compare_with_oranda_default_and_orange_marks_with_the_saved_values()
    {
        var (vm, _) = await LoadedAsync("orange5"); // 시험 저장소의 Orange 5 = 앞 쉼표만 다르다

        var comma = Row(vm, "commaPosition");
        Assert.True(comma.IsChanged);
        Assert.True(comma.ShowsChanged);
        Assert.False(comma.IsDirty);
        Assert.Equal(1, vm.ChangedCount);
        Assert.Contains($"옵션 {FormatterOptionSchema.All.Count}개 중 1개가 기본과 다름", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("기본 제공(읽기 전용)", vm.StatusLine, StringComparison.Ordinal);

        await vm.CopySelectedAsync();
        var keyword = Row(vm, "case.keyword");
        keyword.ChoiceValue = nameof(LetterCase.Lower);

        Assert.True(keyword.IsDirty);
        Assert.True(keyword.IsChanged);
        Assert.False(keyword.ShowsChanged); // 주황이 파랑을 덮는다
        Assert.True(keyword.IsMarked);
        Assert.Equal("저장 안 한 변경", keyword.MarkerTip);
        Assert.Equal(1, vm.DirtyCount);
        Assert.Equal("저장 안 한 변경 1개", vm.DirtyText);
        Assert.Equal(2, vm.ChangedCount);

        keyword.ChoiceValue = nameof(LetterCase.Upper); // 원래 값으로 되돌리면 표시도 사라진다
        Assert.False(keyword.IsMarked);
        Assert.Equal(0, vm.DirtyCount);
        Assert.False(vm.IsDirty);
        Assert.Equal(string.Empty, vm.DirtyText);
    }

    [Fact]
    public async Task Save_clears_orange_marks_but_keeps_blue_ones()
    {
        var (vm, store) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        Row(vm, "case.keyword").ChoiceValue = nameof(LetterCase.Lower);
        Assert.True(vm.CanSave);

        await vm.SaveAsync();

        Assert.False(vm.IsDirty);
        Assert.False(Row(vm, "case.keyword").IsDirty);
        Assert.True(Row(vm, "case.keyword").ShowsChanged);
        Assert.Equal(LetterCase.Lower, store.Items.Single(p => p.Id == "team").Options.Case.Keyword);
        Assert.Contains("저장했습니다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revert_restores_saved_options_and_name_and_the_preview()
    {
        var (vm, store) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        var preview = vm.PreviewText;
        vm.ProfileName = "다른 이름";
        Row(vm, "indent.size").NumberValue = 8;
        Row(vm, "commaPosition").ChoiceValue = nameof(CommaPosition.Trailing);
        Assert.Equal(3, vm.DirtyCount); // 옵션 2 + 이름 1
        Assert.NotEqual(preview, vm.PreviewText);

        vm.Revert();

        Assert.Equal("우리 팀", vm.ProfileName);
        Assert.Equal(2, (int)Row(vm, "indent.size").NumberValue);
        Assert.Equal(nameof(CommaPosition.Leading), Row(vm, "commaPosition").ChoiceValue);
        Assert.Equal(0, vm.DirtyCount);
        Assert.Equal(preview, vm.PreviewText);
        Assert.Equal(Team.Options, store.Items.Single(p => p.Id == "team").Options); // 저장소는 그대로
    }

    [Fact]
    public async Task Changed_marks_use_the_stored_oranda_default_as_the_baseline()
    {
        // 기본 제공 「Oranda 기본」이 하우스 서식(OrandaDefault)이면 그 값과 같은 프로필은 「기본과 다름」 0개다.
        var house = new FormatterProfile { Id = "house", Name = "하우스 복사본", Options = FormatterOptions.OrandaDefault };
        var old = new FormatterProfile { Id = "old", Name = "옛 기본", Options = new FormatterOptions() };
        var store = new MemoryStore([house, old], FormatterOptions.OrandaDefault);
        var vm = new FormatterProfilesViewModel(store, RealFormatter(), new MemorySettings());
        await vm.LoadAsync();

        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "house");
        Assert.Equal(0, vm.ChangedCount);

        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "old");
        Assert.True(vm.ChangedCount > 0);
        Assert.True(Row(vm, "clauseLayout").ShowsChanged);
        Assert.True(Row(vm, "commaPosition").ShowsChanged);
    }

    [Fact]
    public async Task New_options_sit_in_their_groups_and_column_layout_fades_keyword_align()
    {
        var (vm, _) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        Assert.Equal(FormatterOptionSchema.CommaGroup, Row(vm, "clauseLayout").Group);
        Assert.Equal(FormatterOptionSchema.LineBreakGroup, Row(vm, "lineBreaks.orderBy").Group);
        Assert.Equal(FormatterOptionSchema.AlignGroup, Row(vm, "align.comparison").Group);
        Assert.Equal(OrderByLabels, Row(vm, "lineBreaks.orderBy").ChoiceItems.Select(c => c.Label));
        Assert.True(Row(vm, "keywordAlign").IsApplicable);
        Assert.False(Row(vm, "align.comparison").IsApplicable); // 들여쓰기 배치에서는 쓰지 않는다

        Row(vm, "clauseLayout").ChoiceValue = nameof(ClauseLayout.Column);

        Assert.False(Row(vm, "keywordAlign").IsApplicable);
        Assert.Contains("쓰지 않습니다", Row(vm, "keywordAlign").Tip, StringComparison.Ordinal);
        Assert.True(Row(vm, "align.comparison").IsApplicable);
        Assert.Equal(Row(vm, "align.comparison").Label, Row(vm, "align.comparison").Tip);
    }

    [Fact]
    public Task View_fades_options_that_have_no_effect() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        var (window, view) = Show(vm, 2600);
        var label = view.OptionsPanel!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "키워드 맞춤");
        Assert.Equal(1.0, label.Opacity);

        Row(vm, "clauseLayout").ChoiceValue = nameof(ClauseLayout.Column);
        Pump(window);

        Assert.True(label.Opacity < 1);
        var cell = OptionControl<Control>(view, "keywordAlign", c => c.Classes.Contains("fmt-option"));
        Assert.True(cell.Opacity < 1);
        Assert.Contains("쓰지 않습니다", (string)ToolTip.GetTip(cell)!, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public async Task Revert_does_nothing_for_built_in_or_clean_profiles()
    {
        var (vm, _) = await LoadedAsync("orange5");
        vm.Revert();
        Assert.Equal(string.Empty, vm.StatusText);

        vm.RevertName();
        Assert.Equal("Orange 5 스타일", vm.ProfileName);
    }

    [Fact]
    public async Task New_profile_starts_from_oranda_default_and_becomes_selected()
    {
        var (vm, store) = await LoadedAsync("orange5");

        await vm.NewAsync();

        Assert.Equal("새 프로필", vm.Selected!.Profile.Name);
        Assert.False(vm.Selected.IsBuiltIn);
        Assert.Equal(new FormatterOptions(), vm.CurrentOptions);
        Assert.Equal(0, vm.ChangedCount);
        Assert.Equal(4, store.Items.Count);

        await vm.NewAsync();
        Assert.Equal("새 프로필 (2)", vm.Selected!.Profile.Name);
    }

    [Fact]
    public async Task List_shows_two_groups_with_a_hint_when_there_are_no_own_profiles()
    {
        var (vm, _) = await LoadedAsync();

        Assert.Equal(new ProfileGroupHeader(FormatterProfilesViewModel.BuiltInHeader), vm.ProfileItems[0]);
        Assert.True(((ProfileGroupHeader)vm.ProfileItems[0]).IsBuiltIn);
        Assert.Equal(["oranda-default", "orange5", "compact"], vm.ProfileItems.Skip(1).Take(3).Cast<ProfileEntry>().Select(e => e.Profile.Id));
        Assert.Equal(new ProfileGroupHeader(FormatterProfilesViewModel.MineHeader), vm.ProfileItems[4]);
        Assert.IsType<ProfileListHint>(vm.ProfileItems[5]);

        await vm.CopySelectedAsync();
        Assert.DoesNotContain(vm.ProfileItems, i => i is ProfileListHint);
        Assert.IsType<ProfileEntry>(vm.ProfileItems[^1]);

        vm.SelectedItem = vm.ProfileItems[0]; // 머리 줄은 고를 수 없다
        Assert.IsType<ProfileEntry>(vm.SelectedItem);
        Assert.Equal("lock", vm.Profiles[0].IconKey);
        Assert.Equal("format", vm.Profiles[^1].IconKey);
    }

    [Fact]
    public async Task Default_star_follows_set_default()
    {
        var (vm, _) = await LoadedAsync("orange5");
        Assert.False(vm.IsDefaultSelected);
        Assert.True(vm.CanSetDefault);
        Assert.Equal("star", vm.Profiles.Single(p => p.Profile.Id == "oranda-default").StarKey);

        await vm.SetDefaultAsync();

        Assert.True(vm.IsDefaultSelected);
        Assert.False(vm.CanSetDefault);
        Assert.Null(vm.Profiles.Single(p => p.Profile.Id == "oranda-default").StarKey);
        Assert.Contains("★ 기본 프로필", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public Task View_shows_the_read_only_bar_for_built_ins_and_copy_to_edit_makes_an_editable_copy() => Ui.Run(async () =>
    {
        var (vm, store) = await LoadedAsync("orange5");
        var (window, view) = Show(vm);
        var bar = view.FindControl<Border>("ReadOnlyBar")!;
        var title = view.FindControl<StackPanel>("TitleLine")!;
        Assert.True(bar.IsEffectivelyVisible);
        Assert.False(title.IsVisible);

        view.FindControl<Button>("CopyToEditButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => vm.Selected is { IsBuiltIn: false });
        Pump(window);

        Assert.Equal("Orange 5 스타일 복사본", vm.Selected!.Profile.Name);
        Assert.Equal(store.Items.Single(p => p.Id == "orange5").Options, vm.CurrentOptions);
        Assert.False(bar.IsVisible);
        Assert.True(title.IsEffectivelyVisible);
        Assert.Equal("Orange 5 스타일 복사본", view.FindControl<TextBox>("NameBox")!.Text);
        Assert.True(view.FindControl<Button>("DeleteButton")!.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task View_clicking_an_option_of_a_built_in_explains_and_changes_nothing_while_own_profiles_change() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5", Team);
        var (window, view) = Show(vm);

        void Click(string path, string label)
        {
            var radio = OptionControl<RadioButton>(view, path, r => r.Content as string == label);
            var p = radio.TranslatePoint(new Point(radio.Bounds.Width / 2, radio.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Pump(window);
        }

        Click("commaPosition", "뒤에");
        Assert.Equal(nameof(CommaPosition.Leading), Row(vm, "commaPosition").ChoiceValue);
        Assert.Equal(FormatterProfilesView.ReadOnlyHint, vm.StatusText);
        Assert.False(vm.IsDirty);

        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        Pump(window);
        Click("commaPosition", "뒤에");
        Assert.Equal(nameof(CommaPosition.Trailing), Row(vm, "commaPosition").ChoiceValue);
        Assert.True(vm.IsDirty);
        Assert.True(OptionControl<RadioButton>(view, "commaPosition", r => r.Content as string == "뒤에").IsChecked);
        Assert.False(OptionControl<RadioButton>(view, "commaPosition", r => r.Content as string == "앞에").IsChecked);
        window.Close();
    });

    [Fact]
    public Task View_markers_show_blue_for_changed_and_orange_for_unsaved_and_labels_turn_bold() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        var (window, view) = Show(vm);
        Ellipse Blue(string path) => (Ellipse)MarkerOf(view, path).Children[0];
        Ellipse Orange(string path) => (Ellipse)MarkerOf(view, path).Children[1];

        Assert.True(Blue("commaPosition").IsVisible); // 팀 = 앞 쉼표
        Assert.False(Orange("commaPosition").IsVisible);
        Assert.False(Blue("case.keyword").IsVisible);

        Row(vm, "lineBreaks.maxLineWidth").NumberValue = 100;
        Pump(window);

        Assert.True(Orange("lineBreaks.maxLineWidth").IsVisible);
        Assert.False(Blue("lineBreaks.maxLineWidth").IsVisible);
        var label = view.OptionsPanel!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "최대 줄 너비");
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, label.FontWeight);
        Assert.Equal("저장 안 한 변경 1개", vm.DirtyText);
        Assert.True(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.True(view.FindControl<Button>("RevertButton")!.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task View_has_a_control_for_every_option_and_shows_korean_values() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5");
        var (window, view) = Show(vm);
        var panel = view.OptionsPanel!;

        var paths = panel.GetVisualDescendants().OfType<Control>().SelectMany(PathsOf).Distinct().OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(FormatterOptionSchema.All.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal), paths);

        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
        Assert.Empty(texts.Intersect(EnumNames));
        foreach (var ko in KoreanValues)
        {
            Assert.Contains(ko, texts);
        }

        Assert.Equal(FormatterOptionSchema.Groups, panel.Children.Select(c => c.Tag as string));
        window.Close();
    });

    [Theory]
    [InlineData(1000, 3)]
    [InlineData(976, 3)]
    [InlineData(900, 2)]
    [InlineData(668, 2)]
    [InlineData(600, 1)]
    public Task Group_panel_uses_three_two_or_one_columns_from_the_natural_widths(double width, int columns) => Ui.Run(() =>
    {
        // 묶음 본래 폭: 3단 = 300 + 360 + 300 + 간격 16 = 976, 2단 = 300 + 360 + 8 = 668.
        var widths = new Dictionary<string, double>
        {
            [FormatterOptionSchema.CaseGroup] = 250,
            [FormatterOptionSchema.IndentGroup] = 300,
            [FormatterOptionSchema.LineBreakGroup] = 360,
            [FormatterOptionSchema.CommaGroup] = 280,
            [FormatterOptionSchema.AlignGroup] = 290,
            [FormatterOptionSchema.EtcGroup] = 300,
        };
        var panel = new OptionGroupsPanel();
        foreach (var (group, w) in widths)
        {
            panel.Children.Add(new Border { Tag = group, Width = w, Height = 100 });
        }

        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));

        Assert.Equal(columns, panel.Columns);
        Assert.Equal(976, panel.RequiredWidth(3));
        Assert.Equal(668, panel.RequiredWidth(2));
        Assert.Equal(Plans[columns - 1], panel.ColumnGroups.Select(c => c.ToArray()));
        Assert.Equal(columns switch { 3 => 208, 2 => 316, _ => 640 }, panel.DesiredSize.Height); // 높이 100 상자 + 사이 8
        Assert.All(panel.Children, c => Assert.True(c.Bounds.X >= 0 && c.Bounds.X < width));
    });

    [Fact]
    public Task View_drops_to_two_then_one_column_when_the_window_narrows_without_clipping_labels() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5");
        var (window, view) = Show(vm, 2600);
        var panel = view.OptionsPanel!;
        Assert.Equal(3, panel.Columns);
        Assert.Empty(TextClipping.Find(window));
        var chrome = window.Bounds.Width - panel.Bounds.Width; // 목록·여백
        var need2 = panel.RequiredWidth(2);
        var need3 = panel.RequiredWidth(3);
        Assert.True(need2 < need3);

        // 3단이 겨우 들어가는 폭(단마다 남는 폭 없음)에서도 라벨이 잘리지 않는다.
        window.Width = chrome + need3 + 1;
        Pump(window);
        Assert.Equal(3, panel.Columns);
        Assert.Empty(TextClipping.Find(window));

        window.Width = chrome + ((need2 + need3) / 2);
        Pump(window);
        Assert.Equal(2, panel.Columns);
        Assert.Empty(TextClipping.Find(window));
        Assert.All(panel.Children, box => Assert.True(box.Bounds.Right <= panel.Bounds.Width + 0.75, $"{box.Tag} 가 판 밖으로 나간다"));

        window.Width = chrome + need2 + 1;
        Pump(window);
        Assert.Equal(2, panel.Columns);
        Assert.Empty(TextClipping.Find(window));

        window.Width = chrome + need2 - 40;
        Pump(window);
        Assert.Equal(1, panel.Columns);
        Assert.All(panel.Children, box => Assert.True(box.Bounds.Right <= panel.Bounds.Width + 0.75, $"{box.Tag} 가 판 밖으로 나간다"));
        window.Close();
    });

    [Fact]
    public Task View_context_menu_offers_profile_actions_and_disables_what_built_ins_cannot_do() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5");
        var (window, view) = Show(vm);
        var menu = view.FindControl<ListBox>("ProfileList")!.ContextMenu!;
        var items = menu.Items.OfType<MenuItem>().ToDictionary(m => (string)m.Header!);

        Assert.Equal(MenuHeaders, items.Keys);
        Assert.True(items["복사해서 새 프로필"].IsEnabled);
        Assert.False(items["이름 바꾸기"].IsEnabled);
        Assert.False(items["삭제"].IsEnabled);
        Assert.True(items["기본으로 지정"].IsEnabled);
        Assert.True(items["내보내기…"].IsEnabled);
        Assert.All(items.Values, m => Assert.NotNull(m.Icon));

        items["복사해서 새 프로필"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await HeadlessHost.WaitUntil(() => vm.Selected is { IsBuiltIn: false });
        Pump(window);
        Assert.True(items["이름 바꾸기"].IsEnabled);
        Assert.True(items["삭제"].IsEnabled);
        window.Close();
    });

    [Fact]
    public Task View_right_click_picks_the_profile_under_the_pointer_for_the_menu() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5", Team);
        var (window, view) = Show(vm);
        var list = view.FindControl<ListBox>("ProfileList")!;
        var team = vm.Profiles.Single(p => p.Profile.Id == "team");
        var item = (ListBoxItem)list.ContainerFromItem(team)!;
        var p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

        window.MouseDown(p, MouseButton.Right);
        window.MouseUp(p, MouseButton.Right);
        Pump(window);

        Assert.Same(team, vm.Selected);
        Assert.True(view.FindControl<MenuItem>("MenuDelete")!.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task View_list_headers_cannot_be_selected_and_f2_focuses_the_name_box() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync(null, Team);
        vm.Selected = vm.Profiles.Single(p => p.Profile.Id == "team");
        var (window, view) = Show(vm);
        var list = view.FindControl<ListBox>("ProfileList")!;
        var header = (ListBoxItem)list.ContainerFromIndex(0)!;
        Assert.False(header.IsEnabled);
        Assert.True(((ListBoxItem)list.ContainerFromIndex(1)!).IsEnabled);
        Assert.Same(vm.Selected, list.SelectedItem);

        ((ListBoxItem)list.ContainerFromItem(vm.Selected!)!).Focus();
        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Pump(window);
        var name = view.FindControl<TextBox>("NameBox")!;
        Assert.True(name.IsFocused);

        name.Text = "고친 이름";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump(window);
        Assert.Equal("우리 팀", vm.ProfileName); // Esc = 되돌림
        window.Close();
    });

    [Fact]
    public Task View_sample_editor_toggles_and_edits_refresh_the_preview() => Ui.Run(async () =>
    {
        var (vm, _) = await LoadedAsync("orange5");
        var (window, view) = Show(vm);
        var sample = view.FindControl<TextBox>("SampleBox")!;
        Assert.False(sample.IsVisible);
        Assert.Contains("예제 SQL 5줄", vm.PreviewInfo, StringComparison.Ordinal);

        view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SampleToggle")!.IsChecked = true;
        Pump(window);
        Assert.True(vm.IsSampleEditing);
        Assert.True(sample.IsEffectivelyVisible);

        sample.Text = "select 1 from dual";
        Pump(window);
        Assert.StartsWith("SELECT 1", view.FindControl<SqlCodeView>("PreviewBox")!.Text, StringComparison.Ordinal);
        Assert.Contains("예제 SQL 1줄", vm.PreviewInfo, StringComparison.Ordinal);
        window.Close();
    });

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

    private sealed class MemoryStore(IEnumerable<FormatterProfile> mine, FormatterOptions? orandaDefault = null) : IFormatterProfileStore
    {
        private readonly FormatterProfile[] _builtIns =
        [
            new() { Id = "oranda-default", Name = "Oranda 기본", Options = orandaDefault ?? new FormatterOptions() },
            new() { Id = "orange5", Name = "Orange 5 스타일", Options = new FormatterOptions { CommaPosition = CommaPosition.Leading } },
            new() { Id = "compact", Name = "간결", Options = new FormatterOptions { Indent = new IndentOptions { Size = 2 } } },
        ];

        private readonly List<FormatterProfile> _user = [.. mine];

        public IReadOnlyList<FormatterProfile> Items => [.. _builtIns, .. _user];

        public Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct) => Task.FromResult(Items);

        public Task SaveAsync(FormatterProfile profile, CancellationToken ct)
        {
            if (Items.Any(p => p.Id != profile.Id && p.Name == profile.Name))
            {
                throw new InvalidOperationException("같은 이름의 프로필이 이미 있다: " + profile.Name);
            }

            var at = _user.FindIndex(p => p.Id == profile.Id);
            if (at >= 0)
            {
                _user[at] = profile;
            }
            else
            {
                _user.Add(profile);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            _user.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }

        public Task ExportAsync(string id, string path, CancellationToken ct) => Task.CompletedTask;

        public Task<FormatterProfile> ImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
