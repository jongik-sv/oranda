using Avalonia.Controls;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>
/// TSK-13-44: 설정 「변경 추적하기」(grid.flashbackTrackEnabled)를 끄면 그리드 「플래시백 ▸ 이 행 변경 이력」 만 우클릭 메뉴·명령 팔레트·레지스트리
/// (단축키도 같은 레지스트리를 본다)에서 숨고, 다른 플래시백 명령은 그대로다. 다시 켜면 돌아온다(Headless 셸).
/// </summary>
public sealed class FlashbackTrackSwitchTests
{
    private const string History = "oranda.grid.flashback.history";
    private const string AsOf = "oranda.grid.flashback.asOf";
    private const string Restore = "oranda.grid.flashback.restore";

    private static IEnumerable<string> GridContextIds()
    {
        static IEnumerable<string> Walk(IEnumerable<object> items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Command is RegistryCommand { CommandId: { } id })
                {
                    yield return id;
                }

                foreach (var inner in Walk(item.Items.OfType<object>()))
                {
                    yield return inner;
                }
            }
        }

        return Walk(ShellContextMenus.Factory!("grid"));
    }

    private static HashSet<string> PaletteIds(ShellHarness h) =>
        new CommandPaletteViewModel(h.Get<ICommandRegistry>(), h.Get<IContextKeys>(), h.Get<ShellController>().Text, _ => null)
            .Search(string.Empty).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public Task Turning_track_changes_off_hides_only_the_row_history_command() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var commands = h.Get<ICommandRegistry>();
        Assert.True(settings.Current.Grid.FlashbackTrackEnabled); // 기본 켬
        Assert.Contains(History, GridContextIds());
        Assert.Contains(History, PaletteIds(h));

        await settings.UpdateAsync(s => s with { Grid = s.Grid with { FlashbackTrackEnabled = false } });
        await HeadlessHost.WaitUntil(() => commands.Find(History) is null);

        var context = GridContextIds().ToList();
        Assert.DoesNotContain(History, context);
        Assert.Contains(AsOf, context);
        Assert.Contains(Restore, context);
        var palette = PaletteIds(h);
        Assert.DoesNotContain(History, palette);
        Assert.Contains(AsOf, palette);
        Assert.False(commands.CanExecute(History, h.Get<IContextKeys>()));
        Assert.True(((CommandRegistry)commands).IsFeatureDisabled(History)); // 키맵은 꺼진 명령의 키를 흘려보낸다

        // 다른 그리드 설정만 바꾸면 그대로 숨어 있고, 다시 켜면 돌아온다.
        await settings.UpdateAsync(s => s with { Grid = s.Grid with { PageSize = 300 } });
        Assert.Null(commands.Find(History));
        await settings.UpdateAsync(s => s with { Grid = s.Grid with { FlashbackTrackEnabled = true } });
        await HeadlessHost.WaitUntil(() => commands.Find(History) is not null);
        Assert.Contains(History, GridContextIds());
    });

    [Fact]
    public Task Track_setting_saved_off_is_applied_when_the_shell_starts() => HeadlessHost.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-fbswitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"schemaVersion":1,"grid":{"flashbackTrackEnabled":false}}""");
        try
        {
            using var h = new ShellHarness(dir);

            Assert.Null(h.Get<ICommandRegistry>().Find(History));
            Assert.NotNull(h.Get<ICommandRegistry>().Find(AsOf));
            Assert.DoesNotContain(History, GridContextIds());
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    });
}
