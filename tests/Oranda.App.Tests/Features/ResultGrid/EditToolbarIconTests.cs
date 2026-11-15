using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell.Icons;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// 결과 그리드 편집 툴바의 행 추가·복제·삭제 아이콘(gc-w2-icons): 표 전체가 아니라 「한 줄」 모양 전용 키를 쓰고,
/// 아이콘만 보이는 단추라 툴팁이 단추 글 그대로 나온다. 데이터 복사 「테이블 추가」 는 예전 표 아이콘 그대로.
/// </summary>
public sealed class EditToolbarIconTests
{
    private static readonly (string Command, string Key, string Label)[] RowButtons =
    [
        ("oranda.grid.edit.addRow", "gridRowAdd", "행 추가"),
        ("oranda.grid.edit.duplicateRow", "gridRowDuplicate", "행 복제"),
        ("oranda.grid.edit.pasteRows", "rowPaste", "행 붙여넣기"),
        ("oranda.grid.edit.deleteRow", "gridRowDelete", "행 삭제"),
    ];

    [Fact]
    public void Row_icon_keys_are_registered_and_the_data_copy_table_keys_stay()
    {
        foreach (var (_, key, _) in RowButtons)
        {
            Assert.True(ShellIcons.Contains(key), key);
        }

        Assert.True(ShellIcons.Contains("rowAdd")); // 데이터 복사 「테이블 추가」
        Assert.True(ShellIcons.Contains("rowDelete")); // 데이터 복사 「규칙 제거」
    }

    [Fact]
    public Task Edit_toolbar_row_buttons_show_row_icons_with_their_label_as_tooltip() => HeadlessHost.Run(async () =>
    {
        var view = new SqlResultPaneView { DataContext = new SqlResultPaneViewModel() };
        var window = new Window { Content = view, Width = 700, Height = 300 };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var buttons = view.GetLogicalDescendants().OfType<Button>().ToList();
            foreach (var (command, key, label) in RowButtons)
            {
                var button = Assert.Single(buttons, b => Equals(b.Tag, command));
                Assert.Equal(key, ButtonIcon.GetKey(button));
                Assert.IsType<Viewbox>(button.Content); // 아이콘이 실제로 붙음(키가 비거나 모르는 키면 글자로 남는다)
                Assert.Equal(label, ToolTip.GetTip(button));
            }

            Render(window, buttons.First(b => Equals(b.Tag, "oranda.grid.edit.addRow")).FindLogicalAncestorOfType<Border>()!);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary><c>ORANDA_RENDER_DIR</c> 가 있을 때만 편집 툴바 부분을 밝게·어둡게 PNG 로 저장한다(눈 확인용, 2배 크기).</summary>
    private static void Render(Window window, Border toolbar)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        toolbar.IsVisible = true; // 편집 세션 없이 툴바만 보이게(그림용)
        foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            window.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var panel = (Control)toolbar.Child!;
            var size = new PixelSize((int)(panel.Bounds.Width + 16) * 2, (int)(toolbar.Bounds.Height * 2));
            using var bmp = new RenderTargetBitmap(size, new Vector(192, 192));
            bmp.Render(toolbar);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"edit-toolbar-{tone}.png"));
#pragma warning restore CS0618
        }
    }
}
