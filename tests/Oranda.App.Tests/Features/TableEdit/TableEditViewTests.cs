using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>객체 상세 화면의 편집 모드(Headless, 시안 화면 0 A): ✎ 토글 → 편집 표·아래 DDL, 줄 색 표시, 끄기 거절 시 토글이 켠 채.</summary>
public class TableEditViewTests
{
    [Fact]
    public Task Edit_toggle_swaps_the_read_table_for_the_edit_table_with_ddl_below() => HeadlessHost.Run(async () =>
    {
        var (vm, _, probe) = CreateDetail();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1200, Height = 760 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var toggle = view.FindControl<ToggleButton>("TableEditToggle")!;
            var section = view.FindControl<Control>("SectionGrid")!;
            var edit = view.FindControl<TableEditView>("TableEditView")!;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.True(section.IsEffectivelyVisible);
            Assert.False(edit.IsEffectivelyVisible);

            toggle.IsChecked = true;
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => vm.IsTableEditing);
            HeadlessHost.Settle(w);

            Assert.False(section.IsEffectivelyVisible);
            Assert.True(edit.IsEffectivelyVisible);
            var rows = edit.FindControl<ListBox>("ColumnRows")!;
            Assert.Equal(4, rows.ItemCount);
            Assert.True(view.FindControl<Control>("TableEditDdlView")!.IsEffectivelyVisible);
            Assert.Equal("바뀐 것 없음", view.FindControl<TextBlock>("TableEditSummaryText")!.Text);
            Assert.Equal("개발", view.FindControl<TextBlock>("EnvironmentText")!.Text);
            Assert.False(view.FindControl<Button>("SaveScriptButton")!.IsEnabled);

            // 줄 색: 새 줄 초록(added), 지울 줄 빨강(dropped), 바뀐 칸 노랑(changed).
            vm.TableEdit!.ToggleDrop(vm.TableEdit.Columns[3].Key);
            vm.TableEdit.AddColumn()!.Name = "TOTAL_VAT";
            vm.TableEdit.Columns[2].LengthText = "20";
            HeadlessHost.Settle(w);
            var borders = rows.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row")).ToList();
            Assert.Equal(5, borders.Count);
            Assert.Contains("dropped", borders[3].Classes);
            Assert.Contains("added", borders[4].Classes);
            Assert.Contains(borders[2].GetVisualDescendants().OfType<TextBox>(), t => t.Classes.Contains("changed") && t.Text == "20");
            Assert.Equal("변경 3건", view.FindControl<TextBlock>("TableEditSummaryText")!.Text);
            Assert.True(view.FindControl<Button>("SaveScriptButton")!.IsEnabled);
            Assert.Contains(view.FindControl<ListBox>("SectionList")!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "열 (3)");

            // 끄기를 거절하면 토글은 켠 채로 돌아온다.
            probe.Answer = false;
            toggle.IsChecked = false;
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => probe.Asked.Count == 1);
            HeadlessHost.Settle(w);
            Assert.True(toggle.IsChecked);
            Assert.True(vm.IsTableEditing);

            // 제약 페이지로 옮기면 ＋ ▾ 메뉴 단추가 보이고 표가 제약으로 바뀐다.
            vm.SelectedSection = vm.Sections.Single(s => s.Title == "제약");
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Button>("AddConstraintButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("AddRowButton")!.IsEffectivelyVisible);
            Assert.Equal(2, edit.FindControl<ListBox>("ConstraintRows")!.ItemCount);
            Assert.False(rows.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
        }
    });
}
