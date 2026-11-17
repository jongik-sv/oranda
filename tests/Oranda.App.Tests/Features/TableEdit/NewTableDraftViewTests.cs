using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>작업 저장 화면(Headless, 설계 18.7): 닫기 「버리기 / 남겨 두기 / 취소」 단추 창, 「만들던 새 테이블…」 고르기 창(열기·지우기).</summary>
public class NewTableDraftViewTests
{
    private static Button Click(Window w, string text)
    {
        var button = w.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return button;
    }

    [Fact]
    public Task Close_dialog_has_three_buttons_with_keep_as_default_and_cancel_as_escape() => HeadlessHost.Run(() =>
    {
        var answer = -2;
        var w = AvaloniaEditorDialogs.BuildButtonsDialog("SAMPLE.ORDER_ITEMS", "만들던 새 테이블을 어떻게 할까요?", ["버리기", "남겨 두기", "취소"], 1, i => answer = i);
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var buttons = w.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(["버리기", "남겨 두기", "취소"], buttons.Select(b => b.Content as string));
            Assert.True(buttons[1].IsDefault);
            Assert.True(buttons[2].IsCancel);
            Assert.False(buttons[0].IsDefault || buttons[0].IsCancel);
            Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "SAMPLE.ORDER_ITEMS");

            Click(w, "버리기");
            Assert.Equal(0, answer);
        }
        finally
        {
            w.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Picker_window_lists_drafts_and_opens_or_deletes_the_selected_one() => HeadlessHost.Run(async () =>
    {
        var deleted = new List<long>();
        TableDraftEntry Entry(long id, string name, string? openBy) =>
            new(id, "dev-example", Dbms.Oracle, "SAMPLE", name, DateTimeOffset.UnixEpoch.AddDays(id), openBy);
        var vm = new TableDraftPickerViewModel("SAMPLE", [Entry(2, "B", "300@1"), Entry(1, "A", null)], (e, _) =>
        {
            deleted.Add(e.Id);
            return Task.FromResult(true);
        });
        var w = new TableDraftPickerWindow { DataContext = vm };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var list = w.FindControl<ListBox>("DraftList")!;
            Assert.Equal(2, list.ItemCount);
            Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "끝난 앱에서 열려 있던 것");

            Click(w, "지우기");
            await HeadlessHost.WaitUntil(() => deleted.Count == 1);
            HeadlessHost.Settle(w);
            Assert.Equal([2L], deleted);
            Assert.Equal(1, list.ItemCount);

            Click(w, "열기");
            HeadlessHost.Settle(w);
            Assert.Equal(1, vm.Chosen!.Id);
            Assert.False(w.IsVisible); // 열기 = 창 닫힘
        }
        finally
        {
            w.Close();
        }
    });
}
