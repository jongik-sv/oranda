using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Tests.Styles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>TSK-13-40: 테이블 레이아웃 창 — 감사 컬럼 빼기(와일드카드·대소문자·빈 목록·넣기·설정 저장), 칸 너비 고정, 첫 창 높이(약 30행).</summary>
[Collection("UiLanguage")]
public class TableLayoutAuditTests
{
    private const string Audit = "REG_DT, REG_ID\nupd_dt;UPD_ID, *_USER_ID";

    private static TableLayoutRow R(int i, string name, string? def = null, bool pk = false) => new(i, name, "VARCHAR2(20)", false, def, null, pk);

    private static readonly TableLayoutRow[] Rows =
    [
        R(1, "ID", pk: true), R(2, "NAME"), R(3, "REG_DT"), R(4, "REG_ID"), R(5, "UPD_DT"), R(6, "UPD_ID"), R(7, "CRT_USER_ID"), R(8, "MEMO"),
    ];

    [Theory]
    [InlineData("REG_DT", true)]
    [InlineData("reg_dt", true)]
    [InlineData("UPD_ID", true)]
    [InlineData("CRT_USER_ID", true)]
    [InlineData("_USER_ID", true)]
    [InlineData("USER_ID", false)]
    [InlineData("REG_DTX", false)]
    [InlineData("NAME", false)]
    public void Wildcards_match_names_ignoring_case(string name, bool expected)
        => Assert.Equal(expected, TableLayoutAudit.Matches(TableLayoutAudit.Parse(Audit), name));

    [Fact]
    public void Question_mark_is_one_character_and_an_empty_list_matches_nothing()
    {
        Assert.True(TableLayoutAudit.Matches(TableLayoutAudit.Parse("UPD_D?"), "UPD_DT"));
        Assert.False(TableLayoutAudit.Matches(TableLayoutAudit.Parse("UPD_D?"), "UPD_D"));
        Assert.Empty(TableLayoutAudit.Parse("  \n , ; "));
        Assert.False(TableLayoutAudit.Matches(TableLayoutAudit.Parse(string.Empty), "REG_DT"));
        Assert.False(TableLayoutAudit.Matches(TableLayoutAudit.Parse(null), "REG_DT"));
    }

    [Fact]
    public void Options_carry_the_audit_fields_to_and_from_settings()
    {
        var o = TableLayoutOptions.From(new TableLayoutSettings { HideAudit = true, AuditColumns = "A*" });
        Assert.True(o.HideAudit);
        Assert.Equal("A*", o.AuditColumns);
        Assert.Equal(new TableLayoutSettings { HideAudit = true, AuditColumns = "A*" }, o.ToSettings());
        Assert.False(TableLayoutOptions.From(new TableLayoutSettings()).HideAudit);
    }

    private static (SqlEditorViewModel Tab, TableLayoutWindow Window, List<TableLayoutSettings> Saved) Open(TableLayoutOptions options, IReadOnlyList<TableLayoutRow>? rows = null)
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select ");
        tab.UpdateCaret(7, 7, 0);
        var saved = new List<TableLayoutSettings>();
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.T", rows ?? Rows), options, saved.Add);
        var window = new TableLayoutWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (tab, window, saved);
    }

    private static string[] Names(TableLayoutWindow w) => w.VisibleRows.Select(r => r.Name).ToArray();

    private static TableLayoutOptions Opts(string audit, bool hide = false) => new TableLayoutOptions(string.Empty, false, false, false, false) { AuditColumns = audit, HideAudit = hide };

    [Fact]
    public Task Check_hides_matching_columns_keeps_original_numbers_and_remembers_the_state() => HeadlessHost.Run(() =>
    {
        var (_, w, saved) = Open(Opts(Audit));
        try
        {
            Assert.True(w.AuditBox.IsEnabled);
            Assert.Equal(8, Names(w).Length);

            w.AuditBox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["ID", "NAME", "MEMO"], Names(w));
            Assert.Equal([1, 2, 8], w.VisibleRows.Select(r => r.Index).ToArray());
            Assert.Contains("5개 숨김", w.StatusText);
            Assert.True(saved.Last().HideAudit);

            w.AuditBox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, Names(w).Length);
            Assert.DoesNotContain("숨김", w.StatusText);
            Assert.False(saved.Last().HideAudit);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Select_all_and_insert_use_only_the_visible_columns() => HeadlessHost.Run(() =>
    {
        var (tab, w, _) = Open(Opts(Audit, hide: true));
        try
        {
            Assert.Equal(["ID", "NAME", "MEMO"], Names(w));
            w.Select(w.VisibleRows);
            Assert.True(w.PasteSelection());
            Assert.Equal("select ID, NAME, MEMO", tab.Text);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Empty_audit_list_leaves_the_check_off_and_grey_with_a_hint() => HeadlessHost.Run(() =>
    {
        var (_, w, _) = Open(Opts(string.Empty, hide: true));
        try
        {
            Assert.False(w.AuditBox.IsEnabled);
            Assert.NotEqual(true, w.AuditBox.IsChecked);
            Assert.Equal("설정에서 감사 컬럼을 정하세요", ToolTip.GetTip(w.AuditBox));
            Assert.Equal(8, Names(w).Length);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Widths_include_far_rows_and_scroll_and_filter_changes_never_reflow() => HeadlessHost.Run(() =>
    {
        var rows = Enumerable.Range(1, 60).Select(i => R(i, i == 55 ? "A_VERY_LONG_COLUMN_NAME_THAT_IS_FAR_DOWN" : "C" + i, i == 57 ? "'DEFAULT-VALUE-ABC'" : null)).ToList();
        var (_, w, _) = Open(Opts(string.Empty), rows);
        try
        {
            var before = GridCFixedWidthTests.HeaderWidths(w);
            Assert.True(before[1] > 200, "긴 이름이 처음부터 반영돼야 한다: " + before[1]);
            GridCFixedWidthTests.ScrollToEnd(w.List);
            Assert.Equal(before, GridCFixedWidthTests.HeaderWidths(w));

            w.FilterBox.Text = "C1";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, GridCFixedWidthTests.HeaderWidths(w));
        }
        finally
        {
            w.Close();
        }
    });

    [Theory]
    [InlineData(100, 1080, 96 + (30 * 24))]
    [InlineData(5, 1080, 96 + (5 * 24))]
    [InlineData(1, 1080, 180)]
    [InlineData(100, 600, 540)]
    [InlineData(100, 0, 96 + (30 * 24))]
    public void Initial_height_fits_about_30_rows_fewer_for_small_tables_and_the_screen(int columns, double work, double expected)
        => Assert.Equal(expected, TableLayoutWindow.InitialHeight(columns, work));

    [Fact]
    public Task First_screen_shows_about_30_rows() => HeadlessHost.Run(() =>
    {
        var rows = Enumerable.Range(1, 80).Select(i => R(i, "COL" + i)).ToList();
        var (_, w, _) = Open(Opts(string.Empty), rows);
        try
        {
            var viewport = w.List.GetVisualDescendants().OfType<ScrollViewer>().First().Viewport.Height;
            Console.WriteLine($"HEIGHT window={w.Height} bounds={w.Bounds.Height} viewport={viewport}");
            Assert.InRange(viewport, TableLayoutWindow.InitialRows * TableLayoutWindow.RowHeight, (TableLayoutWindow.InitialRows * TableLayoutWindow.RowHeight) + (2 * TableLayoutWindow.RowHeight));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public async Task Audit_columns_setting_saves_and_reopens_without_touching_the_check_state()
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        await settings.UpdateAsync(s => s with { TableLayout = new TableLayoutSettings { HideAudit = true, ShowPk = true } });
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"));
        Assert.Equal(string.Empty, vm.AuditColumns);

        vm.AuditColumns = "  REG_DT\nUPD_*  ";
        await vm.SaveAsync();
        Assert.Equal("REG_DT\nUPD_*", store.Saved.TableLayout.AuditColumns);
        Assert.True(store.Saved.TableLayout.HideAudit);
        Assert.True(store.Saved.TableLayout.ShowPk);

        var again = new SettingsViewModel(settings, new ShellText(language: "ko"));
        Assert.Equal("REG_DT\nUPD_*", again.AuditColumns);
    }
}
