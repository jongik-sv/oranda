using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>고친 SQL 탭 닫기 확인 대화상자의 단추와 키(Enter = 저장, Esc = 취소).</summary>
public sealed class SaveChangesDialogTabCloseTests
{
    private static async Task<(SaveChoice? Answer, bool Closed)> RunAsync(Action<Window> interact)
    {
        SaveChoice? answer = null;
        var closed = false;
        await HeadlessHost.Run(() =>
        {
            var dialog = AvaloniaEditorDialogs.BuildSaveChangesDialog("a.sql", c => answer = c);
            dialog.Closed += (_, _) => closed = true;
            dialog.Show();
            interact(dialog);
        });
        return (answer, closed);
    }

    [Fact]
    public async Task Enter_saves()
    {
        var (answer, closed) = await RunAsync(w => w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null));

        Assert.Equal(SaveChoice.Save, answer);
        Assert.True(closed);
    }

    [Fact]
    public async Task Escape_cancels()
    {
        var (answer, closed) = await RunAsync(w => w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null));

        Assert.Equal(SaveChoice.Cancel, answer);
        Assert.True(closed);
    }

    [Fact]
    public async Task Dont_save_button_answers_dont_save()
    {
        var (answer, _) = await RunAsync(w =>
        {
            var button = w.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "저장 안 함"));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });

        Assert.Equal(SaveChoice.DontSave, answer);
    }
}
