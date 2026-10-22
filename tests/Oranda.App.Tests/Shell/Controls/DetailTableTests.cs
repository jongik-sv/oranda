using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>DetailTable(C안 격자): 행 선택(클릭·Shift·Ctrl), Ctrl+C TSV, 머리글 포함, RowActivated, 빈 표, 현재 행 표시, 상태 줄.</summary>
public class DetailTableTests
{
    private static readonly string[] Columns = ["이름", "값", "메모"];

    private static readonly IReadOnlyList<IReadOnlyList<object?>> Data =
    [
        new object?[] { "SGA", 1024, "메모\t하나" },
        new object?[] { "PGA", 2048L, null },
        new object?[] { "OPEN", new DateTime(2026, 10, 22, 9, 30, 0), "줄\n바꿈" },
        new object?[] { "한글;'\"", 3.5m, "끝" },
    ];

    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static (Window Window, DetailTable Table) Open(IReadOnlyList<IReadOnlyList<object?>>? rows = null, IReadOnlyList<string>? columns = null)
    {
        var table = new DetailTable { Columns = columns ?? Columns, Rows = rows ?? Data };
        var window = new Window { Content = table, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, table);
    }

    private static ListBoxItem Item(DetailTable t, int index) => t.List.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(index);

    private static void Click(Window w, DetailTable t, int index, RawInputModifiers mods = RawInputModifiers.None)
    {
        var item = Item(t, index);
        var p = item.TranslatePoint(new Point(40 + index, item.Bounds.Height / 2), w)!.Value;
        w.MouseDown(p, MouseButton.Left, mods);
        w.MouseUp(p, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Rows_are_listed_with_a_header_and_a_status_line() => HeadlessHost.Run(() =>
    {
        var (w, t) = Open();
        try
        {
            Assert.Equal(Data.Count, t.List.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.Equal("4행", t.StatusText);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Click_shift_and_ctrl_select_whole_rows() => HeadlessHost.Run(() =>
    {
        var (w, t) = Open();
        try
        {
            Click(w, t, 3);
            Click(w, t, 0, Primary);
            Assert.Equal(["SGA", "한글;'\""], t.SelectedRows.Select(r => (string)r[0]!));
            Assert.Equal("2개 선택", t.StatusText.Split(" · ")[1]);

            Click(w, t, 1);
            Click(w, t, 3, RawInputModifiers.Shift);
            Assert.Equal(["PGA", "OPEN", "한글;'\""], t.SelectedRows.Select(r => (string)r[0]!));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Ctrl_C_copies_selected_rows_as_tsv_in_table_order() => HeadlessHost.Run(async () =>
    {
        var (w, t) = Open();
        try
        {
            Click(w, t, 2);
            Click(w, t, 0, Primary);
            t.List.Focus();
            w.KeyPress(Key.C, Primary, PhysicalKey.C, "c");
            await HeadlessHost.WaitUntil(() => true);
            var copied = await w.Clipboard!.TryGetTextAsync();
            Assert.Equal("SGA\t1024\t메모 하나\nOPEN\t2026-10-22 09:30:00\t줄 바꿈\n", copied);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Copy_with_headers_puts_the_column_names_first_and_blank_cells_stay_empty() => HeadlessHost.Run(async () =>
    {
        var (w, t) = Open();
        try
        {
            Click(w, t, 1);
            Assert.Equal("이름\t값\t메모\nPGA\t2048\t\n", t.BuildTsv(withHeaders: true));
            Assert.True(await t.CopyAsync(withHeaders: true));
            Assert.Equal("이름\t값\t메모\nPGA\t2048\t\n", await w.Clipboard!.TryGetTextAsync());
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Copy_with_nothing_selected_does_nothing_and_select_all_selects_every_row() => HeadlessHost.Run(async () =>
    {
        var (w, t) = Open();
        try
        {
            Assert.Equal(string.Empty, t.BuildTsv(withHeaders: true));
            Assert.False(await t.CopyAsync(withHeaders: false));
            Click(w, t, 0);
            w.KeyPress(Key.A, Primary, PhysicalKey.A, "a");
            Assert.Equal(Data.Count, t.SelectedRows.Count);
            Assert.Contains("4개 선택", t.StatusText);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Context_menu_has_copy_and_copy_with_headers() => HeadlessHost.Run(() =>
    {
        var (w, t) = Open();
        try
        {
            var menu = t.List.ContextMenu!;
            Assert.Equal(["복사", "머리글 포함 복사"], menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task RowActivated_fires_with_the_pressed_row_and_marks_it_current() => HeadlessHost.Run(() =>
    {
        var (w, t) = Open();
        try
        {
            var got = new List<IReadOnlyList<object?>>();
            t.RowActivated += (_, row) => got.Add(row);
            Click(w, t, 1);
            Click(w, t, 3, RawInputModifiers.Shift);
            Assert.Equal(["PGA", "한글;'\""], got.Select(r => (string)r[0]!));
            Assert.Same(Data[1], got[0]);
            var markers = t.List.GetVisualDescendants().OfType<TextBlock>().Where(b => b.Text == "▶").ToList();
            Assert.Single(markers);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Empty_or_missing_columns_draw_nothing_and_changing_rows_clears_the_selection() => HeadlessHost.Run(() =>
    {
        var (w, t) = Open(rows: [], columns: Columns);
        try
        {
            Assert.Empty(t.List.GetVisualDescendants().OfType<ListBoxItem>());
            Assert.Equal("0행", t.StatusText);
            Assert.Equal(string.Empty, t.BuildTsv(true));

            t.Rows = Data;
            Dispatcher.UIThread.RunJobs();
            Click(w, t, 0);
            Assert.Single(t.SelectedRows);
            t.Rows = Data.Take(2).ToList();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(t.SelectedRows);

            t.Columns = null;
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Assert.Equal(0, t.List.ItemCount);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Rows_over_the_limit_are_cut_with_a_note_in_the_status_line() => HeadlessHost.Run(() =>
    {
        var rows = Enumerable.Range(0, DetailTable.MaxRows + 5).Select(i => (IReadOnlyList<object?>)new object?[] { "R" + i, i, "x" }).ToList();
        var (w, t) = Open(rows);
        try
        {
            Assert.Contains("5행 더 있음", t.StatusText);
            Assert.StartsWith("2,005행", t.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            w.Close();
        }
    });
}
