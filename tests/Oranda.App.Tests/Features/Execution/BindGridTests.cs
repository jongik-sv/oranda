using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 바인드 변수 창 격자·자료형 서식·블록 복사 붙여 넣기(TSK-03-09).
/// 창 VM 은 시험마다 새 호스트로 만든다(<c>ForHost</c>): 기본 생성자는 프로세스 공용 <c>SqlEditorHost.Current</c> 의 활성 탭 목록을 쓰는데,
/// 병렬로 도는 셸 시험이 await 사이에 Current 를 자기 호스트(활성 탭 있음)로 바꿔 두면 두 VM 이 한 목록을 나눠 써 줄이 섞인다.
/// </summary>
public sealed partial class BindGridTests
{
    private static string? UserMapperPath => Oranda.App.Tests.TestSupport.UserMapper.Path;

    [GeneratedRegex(@"#\{\s*(\w+)")]
    private static partial Regex ParamRegex();

    // ---- 자료형 서식 ----------------------------------------------------------------------

    [Theory]
    [InlineData(BindDataType.Number, " 1,234,567 ", "1234567")]
    [InlineData(BindDataType.Number, "-12.5", "-12.5")]
    [InlineData(BindDataType.Number, "", "")]
    [InlineData(BindDataType.Number, "abc", "abc")]
    [InlineData(BindDataType.Date, "2026-10-23", "2026-10-23 00:00:00")]
    [InlineData(BindDataType.Date, "20261023", "2026-10-23 00:00:00")]
    [InlineData(BindDataType.Date, "2026/10/23 13:05", "2026-10-23 13:05:00")]
    [InlineData(BindDataType.Timestamp, "20261023130501", "2026-10-23 13:05:01")]
    [InlineData(BindDataType.Timestamp, "2026-10-23 13:05:01.25", "2026-10-23 13:05:01.25")]
    [InlineData(BindDataType.Date, "내일", "내일")]
    [InlineData(BindDataType.Varchar2, " 1,234 ", " 1,234 ")]
    [InlineData(BindDataType.Clob, "2026-10-23", "2026-10-23")]
    [InlineData(BindDataType.FixedChar, "ab ", "ab ")]
    public void Normalize_follows_the_type(BindDataType type, string input, string expected) =>
        Assert.Equal(expected, BindValueFormatter.Normalize(type, input));

    [Fact]
    public void Normalize_uses_the_given_date_format() =>
        Assert.Equal("23/10/2026", BindValueFormatter.Normalize(BindDataType.Date, "2026-10-23", "dd/MM/yyyy"));

    [Theory]
    [InlineData(BindDataType.Number, "12a", BindDirection.In, true)]
    [InlineData(BindDataType.Number, "1,234", BindDirection.In, false)]
    [InlineData(BindDataType.Number, "", BindDirection.In, false)]
    [InlineData(BindDataType.Number, "12a", BindDirection.Out, false)]
    [InlineData(BindDataType.Date, "2026-13-45", BindDirection.In, true)]
    [InlineData(BindDataType.Date, "2026-10-23", BindDirection.In, false)]
    [InlineData(BindDataType.Varchar2, "아무거나", BindDirection.In, false)]
    public void Validate_reports_unreadable_values(BindDataType type, string text, BindDirection dir, bool error) =>
        Assert.Equal(error, BindValueFormatter.Validate(type, dir, text) is not null);

    [Fact]
    public void Row_marks_a_bad_number_and_clears_it_when_fixed_or_retyped()
    {
        var row = new BindVariableRow("n") { Type = BindDataType.Number, Text = "x1" };
        Assert.True(row.HasValueError);
        Assert.Contains("x1", row.ValueError, StringComparison.Ordinal);
        row.Text = "1,5";
        Assert.False(row.HasValueError);
        row.Text = "zz";
        row.Type = BindDataType.Varchar2; // 자료형을 바꾸면 다시 검사
        Assert.False(row.HasValueError);
        row.Type = BindDataType.Date;
        Assert.True(row.HasValueError);
    }

    [Fact]
    public void Commit_formats_the_text_but_keeps_empty_as_null_and_bad_values_as_typed()
    {
        var row = new BindVariableRow("n") { Type = BindDataType.Number, Text = "1, 000" };
        row.Commit();
        Assert.Equal("1000", row.Text);
        row.Text = string.Empty;
        row.Commit();
        Assert.Equal(string.Empty, row.Text);
        Assert.Null(row.ToBindValue(out _)!.Value); // 빈 칸 = null
        row.Text = "abc";
        row.Commit();
        Assert.Equal("abc", row.Text);
        Assert.Null(row.ToBindValue(out var error));
        Assert.NotNull(error); // 실행 때 막는 동작은 그대로
    }

    // ---- TSV 복사 ----------------------------------------------------------------------

    [Fact]
    public void Copy_gives_four_column_tsv_without_header_and_header_on_request()
    {
        var store = new BindVariableStore();
        store.Add("ID").Text = "7";
        var b = store.Add("D");
        b.Type = BindDataType.Date;
        b.Direction = BindDirection.InOut;
        b.Text = "2026-10-23 00:00:00";
        Assert.Equal("ID\t7\tVarchar2\tIN\nD\t2026-10-23 00:00:00\tDate\tIN OUT\n", BindTsv.Format(store.Rows));
        Assert.StartsWith("변수\t값\t자료형\t방향\nID\t", BindTsv.Format(store.Rows, withHeader: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_through_tsv_restores_value_type_direction_including_tabs_and_quotes()
    {
        var src = new BindVariableStore();
        var a = src.Add("NOTE");
        a.Text = "줄1\n줄2\t\"탭\"";
        var n = src.Add("N");
        n.Type = BindDataType.Number;
        n.Text = "42";
        n.Direction = BindDirection.InOut;
        var tsv = BindTsv.Format(src.Rows);

        var dst = new BindVariableStore();
        dst.Ensure(["NOTE", "N"]);
        var (applied, skipped) = BindTsv.Apply(dst, tsv);
        Assert.Equal((2, 0), (applied, skipped));
        Assert.Equal(a.Text, dst.Find("NOTE")!.Text);
        Assert.Equal((BindDataType.Number, "42", BindDirection.InOut), (dst.Find("N")!.Type, dst.Find("N")!.Text, dst.Find("N")!.Direction));
    }

    [Fact]
    public void Type_labels_are_english_and_paste_accepts_new_old_and_oracle_names_in_any_case()
    {
        Assert.Equal(
            ["Varchar2", "Number", "Date", "Char", "NVarchar2", "Timestamp", "Timestamp With Time Zone", "Binary_Double", "Clob", "Blob", "Raw", "Boolean", "Json", "Ref Cursor"],
            BindTypeChoice.All.Select(c => c.Label).ToArray());

        var cases = new (string Cell, BindDataType Type)[]
        {
            ("Varchar2", BindDataType.Varchar2), ("VARCHAR2", BindDataType.Varchar2), ("문자열", BindDataType.Varchar2),
            ("number", BindDataType.Number), ("숫자", BindDataType.Number), ("DATE", BindDataType.Date), ("날짜", BindDataType.Date),
            ("CHAR", BindDataType.FixedChar), ("char", BindDataType.FixedChar), ("고정 길이 문자", BindDataType.FixedChar),
            ("Binary_Double", BindDataType.BinaryDouble), ("TIMESTAMP WITH TIME ZONE", BindDataType.TimestampTz), ("Ref Cursor", BindDataType.RefCursor),
        };
        foreach (var (cell, type) in cases)
        {
            Assert.Equal(type, Assert.Single(BindTsv.Parse("A	1	" + cell)).Type);
        }
    }

    [Fact]
    public void Paste_matches_names_ignoring_case_and_colon_skips_unknown_and_accepts_two_columns()
    {
        var store = new BindVariableStore();
        store.Ensure(["ID", "CD"]);
        var (applied, skipped) = BindTsv.Apply(store, ":id\t1\n cd \tX\nNOPE\t9\n\n");
        Assert.Equal((2, 1), (applied, skipped));
        Assert.Equal("1", store.Find("ID")!.Text);
        Assert.Equal("X", store.Find("CD")!.Text);
        Assert.Equal(BindDataType.Varchar2, store.Find("ID")!.Type); // 열이 2개면 자료형·방향은 그대로
    }

    [Fact]
    public void Paste_skips_a_header_line_formats_by_type_and_ignores_unknown_type_and_direction()
    {
        var store = new BindVariableStore();
        store.Ensure(["D", "N"]);
        BindTsv.Apply(store, "변수\t값\t자료형\t방향\nD\t20261023\tDATE\tIN\nN\t1,234\t숫자\t???\n");
        Assert.Equal("2026-10-23 00:00:00", store.Find("D")!.Text);
        Assert.Equal(BindDataType.Date, store.Find("D")!.Type);
        Assert.Equal("1234", store.Find("N")!.Text);
        Assert.Equal(BindDirection.In, store.Find("N")!.Direction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("\t\t\t")]
    public void Paste_of_empty_or_nameless_text_changes_nothing(string text)
    {
        var store = new BindVariableStore();
        store.Add("A").Text = "keep";
        Assert.Equal((0, 0), BindTsv.Apply(store, text));
        Assert.Equal("keep", store.Find("A")!.Text);
    }

    [Fact]
    public void Empty_value_cell_in_paste_makes_null()
    {
        var store = new BindVariableStore();
        store.Add("A").Text = "old";
        BindTsv.Apply(store, "A\t\t문자열\tIN");
        Assert.Equal(string.Empty, store.Find("A")!.Text);
        Assert.Null(store.Find("A")!.ToBindValue(out _)!.Value);
    }

    // ---- 창(VM·View) ----------------------------------------------------------------------

    [Fact]
    public void Copy_and_paste_between_two_stores_with_block_selection_in_the_view_model()
    {
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(["A", "B", "C"]);
        vm.Store.Find("A")!.Text = "1";
        vm.Store.Find("B")!.Text = "2";
        vm.Store.Find("C")!.Text = "3";
        vm.SelectedRows = [vm.Rows[1], vm.Rows[2]];
        var tsv = vm.CopyText();
        Assert.Equal("B\t2\tVarchar2\tIN\nC\t3\tVarchar2\tIN\n", tsv);
        Assert.Contains("2줄", vm.StatusText, StringComparison.Ordinal);

        var other = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        other.Store.Ensure(["B", "X"]);
        Assert.Equal(1, other.PasteText(tsv!));
        Assert.Equal("2", other.Store.Find("B")!.Text);
        Assert.Contains("1개는 건너뜀", other.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_removes_the_whole_block()
    {
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(["A", "B", "C", "D"]);
        vm.Selected = vm.Rows[1];
        vm.SelectedRows = [vm.Rows[1], vm.Rows[2]];
        vm.DeleteCommand.Execute(null);
        Assert.Equal(["A", "D"], vm.Rows.Select(r => r.Name));
        Assert.Equal([1, 2], vm.Rows.Select(r => r.Number));
    }

    [Fact]
    public Task View_uses_the_grid_style_multi_select_and_value_cell_shows_error_and_formats_on_leave() => Ui.Run(() =>
    {
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(["N", "D"]);
        vm.Rows[0].Type = BindDataType.Number;
        vm.Rows[1].Type = BindDataType.Date;
        var view = new BindVariablesView { DataContext = vm };
        var window = new Window { Width = 520, Height = 300, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var list = view.FindControl<ListBox>("RowList")!;
        Assert.Contains("grid-c", list.Classes);
        Assert.Equal(SelectionMode.Multiple, list.SelectionMode);
        Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("grid-c-header"));

        list.SelectedItems!.Add(vm.Rows[0]);
        list.SelectedItems.Add(vm.Rows[1]);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.SelectedRows.Count);

        var boxes = view.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name == "ValueBox").ToList();
        boxes[0].Text = "x1";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var cell = (Border)boxes[0].Parent!;
        Assert.Contains("invalid", cell.Classes);
        Assert.NotNull(ToolTip.GetTip(cell));

        boxes[0].Text = "1,000";
        boxes[1].Text = "20261023";
        boxes[0].Focus();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        boxes[1].Focus(); // 값 칸을 떠난다
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        boxes[0].Focus();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("1000", vm.Rows[0].Text);
        Assert.Equal("2026-10-23 00:00:00", vm.Rows[1].Text);
        Assert.DoesNotContain("invalid", cell.Classes);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dragging_across_rows_selects_the_block_even_when_it_starts_in_a_name_cell() => Ui.Run(() =>
    {
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(["A", "B", "C", "D"]);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Window { Width = 520, Height = 300, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = view.FindControl<ListBox>("RowList")!;

        Avalonia.Point Center(int row, Func<Control, bool> which)
        {
            var target = ((Control)list.ContainerFromIndex(row)!).GetVisualDescendants().OfType<Control>().First(which);
            return target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        }

        Avalonia.Point Head(int row) => Center(row, c => c.Classes.Contains("row-head"));
        Avalonia.Point NameCell(int row) => Center(row, c => c is TextBox { Name: "NameBox" });
        void Drag(Avalonia.Point from, params Avalonia.Point[] through)
        {
            window.MouseDown(from, Avalonia.Input.MouseButton.Left);
            foreach (var p in through)
            {
                window.MouseMove(p, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            }

            window.MouseUp(through[^1], Avalonia.Input.MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Drag(Head(1), Head(2), Head(3));
        Assert.Equal(["B", "C", "D"], vm.SelectedRows.Select(r => r.Name).Order());

        // 이름 칸에서 시작해도 다른 줄로 넘어가면 글 고르기가 아니라 줄 고르기다: Ctrl+C 가 줄을 복사하도록 포커스도 목록으로 옮긴다.
        Drag(NameCell(0), NameCell(1));
        Assert.Equal(["A", "B"], vm.SelectedRows.Select(r => r.Name).Order());
        Assert.IsNotType<TextBox>(window.FocusManager?.GetFocusedElement());
        window.KeyPress(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, "c");
        window.KeyRelease(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, "c");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("바인드 변수 2줄을 복사했습니다.", vm.StatusText);
        window.Close();
        return Task.CompletedTask;
    });


    // ---- 사용자 매퍼(있을 때만) ---------------------------------------------------------------

    /// <summary>대형 매퍼의 #{…} 이름으로 창을 채워 복사 → 다른 목록에 붙여 넣기 왕복. 파일이 없으면 건너뛴다(공개 저장소엔 없음).</summary>
    [Fact]
    public Task User_mapper_params_round_trip_through_the_pane() => Ui.Run(() =>
    {
        if (UserMapperPath is not { } userMapper)
        {
            return Task.CompletedTask; // 건너뜀: 저장소 밖 파일
        }

        var names = ParamRegex().Matches(File.ReadAllText(userMapper)).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        Assert.NotEmpty(names);
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(names);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Window { Width = 520, Height = 600, Content = view };
        window.Show();
        for (var i = 0; i < vm.Rows.Count; i++)
        {
            vm.Rows[i].Type = i % 3 == 1 ? BindDataType.Number : i % 3 == 2 ? BindDataType.Date : BindDataType.Varchar2;
            vm.Rows[i].Text = i % 3 == 1 ? $"{i},000" : i % 3 == 2 ? "20261023" : "v" + i;
            vm.Rows[i].Commit();
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        vm.SelectedRows = [.. vm.Rows];
        var tsv = vm.CopyText()!;

        var other = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        other.Store.Ensure(names);
        Assert.Equal(names.Count, other.PasteText(tsv));
        for (var i = 0; i < names.Count; i++)
        {
            Assert.Equal(vm.Rows[i].Text, other.Rows[i].Text);
            Assert.Equal(vm.Rows[i].Type, other.Rows[i].Type);
        }

        window.UpdateLayout();
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(520, 600));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, "bind-pane.png"));
#pragma warning restore CS0618
        }

        window.Close();
        return Task.CompletedTask;
    });
}
