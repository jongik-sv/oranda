using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-13-11. 프로필 편집기 라벨 색 고르기(견본·사용자 지정 색)와 TNS 별칭 입력 콤보 시험.

/// <summary>라벨 색 단추·펼침 창과 TNS 별칭 콤보(▾ 로 전체 별칭, 직접 입력)가 편집 모델과 맞물린다.</summary>
public sealed class ProfileEditorColorTnsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oranda-color-tns-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Three = """
        TESTB = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = hostb)(PORT = 1522)) (CONNECT_DATA = (SERVICE_NAME = svcb)))
        testa = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = hosta)(PORT = 1521)) (CONNECT_DATA = (SID = orcl)))
        PROD1 = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = hostp)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = prod)))
        """;

    private string Folder(string name, string tnsnames)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tnsnames.ora"), tnsnames);
        return dir;
    }

    private static (Window Window, ProfileEditorView View) Show(ProfileEditorViewModel vm)
    {
        var view = new ProfileEditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.FindControl<T>(name) ?? throw new InvalidOperationException(name + " 없음");

    private static void OpenColorFlyout(ProfileEditorView view)
    {
        var button = Named<DropDownButton>(view, "ColorButton");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
    }

    // ---------- 모델 ----------

    [Fact]
    public void Presets_are_never_red_and_picking_one_sets_the_color()
    {
        var vm = ProfileEditorViewModel.ForNew();
        Assert.True(vm.ColorPresets.Count >= 8);
        Assert.All(vm.ColorPresets, p => Assert.False(LabelColorRules.IsRedFamily(p.Hex), p.Hex));
        Assert.All(vm.ColorPresets, p => Assert.True(LabelColorRules.TryParse(p.Hex, out _, out _, out _), p.Hex));
        Assert.All(vm.ColorPresets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));

        var teal = vm.ColorPresets[2];
        vm.PickColor(teal.Hex);
        Assert.Equal(teal.Hex, vm.ColorText);
        Assert.Equal(teal.Hex, vm.EffectiveColor);

        vm.PickColor(null); // 「기본 색」: 환경 기본색으로 되돌린다
        Assert.Equal(string.Empty, vm.ColorText);
        Assert.Equal("#107C10", vm.EffectiveColor);
    }

    [Fact]
    public void Effective_color_follows_environment_and_red_shows_a_warning()
    {
        var vm = ProfileEditorViewModel.ForNew();
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        vm.Environment = ConnectionEnvironment.Test;
        Assert.Equal("#B65A00", vm.EffectiveColor);
        Assert.Contains(nameof(ProfileEditorViewModel.EffectiveColor), seen);

        vm.ColorText = "#3366cc";
        Assert.Equal("#3366CC", vm.EffectiveColor);
        Assert.Equal("#3366CC", vm.ColorLabel);
        Assert.Equal(string.Empty, vm.ColorWarning);

        vm.ColorText = "#FF0000"; // 입력이든 고르기든 빨강은 경고하고 저장을 막는다
        Assert.NotEmpty(vm.ColorWarning);
        Assert.Contains("빨강", vm.ColorWarning, StringComparison.Ordinal);

        vm.Environment = ConnectionEnvironment.Production; // 운영은 항상 빨강이고 사용자 색은 무시한다
        Assert.Equal("#C42B1C", vm.EffectiveColor);
        Assert.Equal(string.Empty, vm.ColorWarning);
        Assert.False(vm.CanPickColor);
    }

    [Fact]
    public void Hex_is_two_digit_upper_case()
    {
        Assert.Equal("#0A0B0C", LabelColorRules.ToHex(10, 11, 12));
        Assert.Equal("#FFFFFF", LabelColorRules.ToHex(255, 255, 255));
    }

    // ---------- 화면: 라벨 색 ----------

    [Fact]
    public Task Color_button_presets_set_the_color_and_production_disables_the_button() => HeadlessHost.Run(() =>
    {
        var vm = ProfileEditorViewModel.ForNew();
        var (window, view) = Show(vm);
        var button = Named<DropDownButton>(view, "ColorButton");
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Equal(Color.Parse("#107C10"), ((ISolidColorBrush)Named<Border>(view, "ColorSwatch").Background!).Color);

        OpenColorFlyout(view);
        Assert.True(button.Flyout!.IsOpen);
        var presets = Named<ItemsControl>(view, "ColorPresetList").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(vm.ColorPresets.Count, presets.Count);
        Assert.All(presets, b => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))));

        presets[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(vm.ColorPresets[0].Hex, vm.ColorText);
        Assert.False(button.Flyout.IsOpen); // 견본을 고르면 닫힌다
        Assert.Equal(Color.Parse(vm.ColorPresets[0].Hex), ((ISolidColorBrush)Named<Border>(view, "ColorSwatch").Background!).Color);
        Assert.Equal(vm.ColorPresets[0].Hex, Named<TextBlock>(view, "ColorLabelText").Text);

        // 「기본 색」 은 환경 기본색으로 되돌린다.
        OpenColorFlyout(view);
        Named<Button>(view, "ColorDefaultButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(string.Empty, vm.ColorText);

        vm.Environment = ConnectionEnvironment.Production;
        Dispatcher.UIThread.RunJobs();
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal(Color.Parse("#C42B1C"), ((ISolidColorBrush)Named<Border>(view, "ColorSwatch").Background!).Color);
        window.Close();
    });

    [Fact]
    public Task Custom_color_from_the_spectrum_sets_the_color_and_red_is_rejected_like_typing() => HeadlessHost.Run(() =>
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.Name = "x";
        vm.User = "u";
        vm.Host = "h";
        vm.Service = "s";
        var (window, view) = Show(vm);
        OpenColorFlyout(view);

        var spectrum = Named<ColorSpectrum>(view, "ColorSpectrum");
        Assert.NotEmpty(spectrum.GetVisualChildren()); // 색 고르기 테마가 읽혀 틀이 그려진다

        spectrum.Color = Color.FromRgb(0x33, 0x66, 0xCC);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#3366CC", vm.ColorText);
        Assert.Equal("#3366CC", Named<TextBox>(view, "ColorBox").Text);
        Assert.False(Named<TextBlock>(view, "ColorWarningText").IsEffectivelyVisible);
        Assert.Equal("#3366CC", vm.TryBuild(out _)!.Color);

        spectrum.Color = Color.FromRgb(0xE0, 0x10, 0x10);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#E01010", vm.ColorText);
        Assert.True(Named<TextBlock>(view, "ColorWarningText").IsEffectivelyVisible);
        Assert.Null(vm.TryBuild(out var errors));
        Assert.Contains(errors, e => e.Contains("빨강", StringComparison.Ordinal));

        // 입력 칸에 친 색은 견본·고르기에도 보인다.
        vm.ColorText = "#00737A";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#00737A"), spectrum.Color);
        Assert.Equal("#00737A", vm.ColorText);
        window.Close();
    });

    // ---------- 화면: TNS 별칭 콤보 ----------

    [Fact]
    public Task Tns_alias_combo_lists_every_alias_takes_typed_text_and_selected_items() => HeadlessHost.Run(() =>
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.TnsAdmin = Folder("three", Three);
        Assert.Equal(ConnectionMode.TnsAlias, vm.Mode);
        var (window, view) = Show(vm);

        var combo = Named<ComboBox>(view, "TnsAliasBox");
        Assert.True(combo.IsEditable);
        Assert.True(combo.IsEffectivelyVisible);
        Assert.Same(vm.TnsAliases, combo.ItemsSource);

        // 글자를 치면 그 글자가 별칭이 된다(목록에 없어도 된다).
        var input = combo.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_EditableTextBox");
        input.Focus();
        window.KeyTextInput("tes");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("tes", vm.TnsAlias);

        // ▾ 로 펼치면 친 글자와 상관없이 별칭이 모두 보인다(입력 제안 칸은 걸러서 일부만 보였다).
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, combo.GetRealizedContainers().Count());

        // 항목을 고르면 별칭·이름·접속 요약이 채워진다.
        var item = (ComboBoxItem)combo.ContainerFromIndex(vm.TnsAliases.ToList().IndexOf("TESTB"))!;
        combo.SelectedItem = "TESTB";
        combo.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(item);
        Assert.Equal("TESTB", vm.TnsAlias);
        Assert.Equal("TESTB", vm.Name);
        Assert.Equal("→ hostb:1522/svcb", vm.TnsAliasDetail);
        Assert.Equal("TESTB", combo.Text);

        // 모델에서 바꾼 별칭(대소문자가 다른 목록 항목)도 칸에 그대로 보이고 되쓰지 않는다.
        vm.TnsAlias = "prod1";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("prod1", combo.Text);
        Assert.Equal("prod1", vm.TnsAlias);
        window.Close();
    });

    [Fact]
    public Task Blank_tns_alias_on_a_saved_profile_marks_the_combo_missing() => HeadlessHost.Run(() =>
    {
        var vm = ProfileEditorViewModel.From(new ConnectionProfile("a", "scott", "TESTB", TnsAdmin: Folder("three", Three)));
        var (window, view) = Show(vm);
        var combo = Named<ComboBox>(view, "TnsAliasBox");
        Assert.DoesNotContain("missing", combo.Classes);

        vm.TnsAlias = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("missing", combo.Classes);
        window.Close();
    });

    // ---------- 열기만 해서는 바뀌지 않는다 ----------

    [Fact]
    public Task Showing_saved_profiles_does_not_write_back_color_or_alias() => HeadlessHost.Run(() =>
    {
        var three = Folder("three", Three);
        var first = ProfileEditorViewModel.From(new ConnectionProfile("첫째", "scott", "TESTB", TnsAdmin: three, Color: "#3366CC"));
        var second = ProfileEditorViewModel.From(new ConnectionProfile("둘째", "scott", "NOPE", TnsAdmin: three, Environment: ConnectionEnvironment.Test));
        Assert.False(first.IsDirty);
        Assert.False(second.IsDirty);

        var (window, view) = Show(first);
        OpenColorFlyout(view);
        Named<DropDownButton>(view, "ColorButton").Flyout!.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.False(first.IsDirty);
        Assert.Equal("#3366CC", first.ColorText);
        Assert.Equal("TESTB", first.TnsAlias);
        Assert.Equal("첫째", first.Name);

        // 접속 관리자에서 다른 접속을 고를 때처럼 편집 모델을 바꾼다.
        view.DataContext = second;
        Dispatcher.UIThread.RunJobs();
        OpenColorFlyout(view);
        Dispatcher.UIThread.RunJobs();
        Assert.False(second.IsDirty);
        Assert.Equal("NOPE", second.TnsAlias);
        Assert.Equal(string.Empty, second.ColorText);
        Assert.False(first.IsDirty);
        Assert.Equal("TESTB", first.TnsAlias);
        Assert.Equal("#3366CC", first.ColorText);

        // TNS 디렉터리를 바꿔 별칭 목록이 바뀌어도 쳐 둔 별칭은 남는다.
        second.TnsAdmin = Folder("one", "ONLY = (DESCRIPTION = (ADDRESS = (HOST = h1)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = s1)))");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["ONLY"], second.TnsAliases);
        Assert.Equal("NOPE", second.TnsAlias);
        Assert.Equal("NOPE", Named<ComboBox>(view, "TnsAliasBox").Text);
        window.Close();
    });
}
