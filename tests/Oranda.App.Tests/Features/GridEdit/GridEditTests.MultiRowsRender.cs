using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.GridEdit;

public sealed partial class GridEditTests
{
    private static readonly string[] GridKeysOn = ["gridHasResult", "gridHasSelection", "gridEditing", "gridRowsEditable", "gridDirty", "gridQueryResult"];

    /// <summary>
    /// 결과 격자에서 행 셋(연속 둘 + 떨어진 하나)을 골라 오른쪽 클릭 메뉴를 열면 「행 3개 삭제」 처럼 개수가 보인다(ko·en).
    /// <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG(<c>rows-menu-{언어}-{톤}.png</c>)를 쓴다.
    /// </summary>
    [Theory]
    [InlineData("ko", "행 3개 삭제", "행 3개 복제", "행 3개 복사", "행 3개 되돌리기")]
    [InlineData("en", "Delete 3 Rows", "Duplicate 3 Rows", "Copy 3 Rows", "Revert 3 Rows")]
    public Task Context_menu_shows_the_selected_row_count(string language, string delete, string duplicate, string copy, string revert) =>
        HeadlessHost.Run(async () =>
        {
            using var h = new ShellHarness();
            var (pane, m, s, _) = await ManyAsync(24);
            var oldTitles = ShellText.TitleOverride;
            try
            {
                await h.Get<Oranda.App.Shell.Settings.SettingsService>().UpdateAsync(x => x with { Ui = x.Ui with { Language = language } });
                await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == language);
                ShellText.TitleOverride = GridRowTitles.For;

                s!.DeleteRows([9, 10]); // 삭제 표시한 행(취소선·행 머리 표시)과 고친 칸이 함께 보이게
                s.SetCell(4, 1, "EDITED");
                m.SelectRow(1);
                m.SelectRow(2, extend: true);
                m.ToggleRowInSelection(6);

                var keys = (ContextKeyService)h.Get<IContextKeys>();
                keys.SetFocusSlot("sqlGrid");
                foreach (var k in GridKeysOn)
                {
                    keys.Set(k, true);
                }

                var view = new SqlResultPaneView { DataContext = pane };
                var window = new Window { Content = view, Width = 980, Height = 520 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await HeadlessHost.WaitUntil(() => view.FindControl<ResultGridControl>("GridControl")!.Bounds.Height > 0);

                var menu = new ContextMenu { ItemsSource = ShellContextMenus.Factory!("grid"), Placement = PlacementMode.Pointer };
                menu.Open(view.FindControl<ResultGridControl>("GridControl")!);
                Dispatcher.UIThread.RunJobs();
                var headers = menu.Items.OfType<MenuItem>().Concat(menu.Items.OfType<MenuItem>().SelectMany(i => i.Items.OfType<MenuItem>()))
                    .Select(i => i.Header as string).ToList();
                Assert.Contains(delete, headers);
                Assert.Contains(duplicate, headers);
                Assert.Contains(revert, headers);
                Assert.DoesNotContain(headers, x => x is "행 삭제" or "Delete Row"); // 하나일 때의 이름은 아니다

                // 「행 편집」(삭제·복제·되돌리기)과 「복사 형식」(행 복사) 하위 메뉴를 하나씩 열어 그린다.
                foreach (var (name, title) in new[] { ("edit", delete), ("copy", copy) })
                {
                    var sub = menu.Items.OfType<MenuItem>().Single(i => i.Items.OfType<MenuItem>().Any(c => Equals(c.Header, title)));
                    sub.IsSubMenuOpen = true;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains(title, sub.Items.OfType<MenuItem>().Select(i => i.Header as string));
                    Render(window, language, name);
                    sub.IsSubMenuOpen = false;
                    Dispatcher.UIThread.RunJobs();
                }

                menu.Close();
                window.Close();
            }
            finally
            {
                ShellText.TitleOverride = oldTitles;
                pane.Clear();
                await h.Get<Oranda.App.Shell.Settings.SettingsService>().UpdateAsync(x => x with { Ui = x.Ui with { Language = "ko" } });
            }
        });

    private static void Render(Window window, string language, string part)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"rows-menu-{part}-{language}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
