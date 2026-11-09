using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Shell.Settings;

/// <summary>Tibero 드라이버 지정(DU1·DU2): 설정 → 경로 출처, 설정 「드라이버」 탭의 상태 줄·확인·지우기·저장.</summary>
[Collection("UiLanguage")]
public sealed class DriverSettingsPageTests
{
    private sealed class FakeStatus : IDbmsDriverStatus
    {
        public Dbms Dbms => Dbms.Tibero;

        public Func<string?, DriverCheck> OnCheck { get; set; } = _ => DriverCheck.Missing;

        public string? LastChecked { get; private set; } = "(none)";

        public int Checks { get; private set; }

        public DriverCheck Current { get; set; } = DriverCheck.Missing;

        public string? ConfiguredPath => null;

        public bool RestartRequired { get; set; }

        public DriverCheck Check(string? path)
        {
            Checks++;
            LastChecked = path;
            return OnCheck(path);
        }
    }

    private static async Task<(SettingsService Settings, MemorySettingsStore Store)> Create(string? path = null)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        if (path is not null)
        {
            await settings.UpdateAsync(s => s with { Drivers = new DriverSettings { TiberoPath = path } });
        }

        return (settings, store);
    }

    private static DriverSettingsPage Page(SettingsService settings, IDbmsDriverStatus? status, bool tiberoIncluded = true) =>
        new(settings, new ShellText(language: "ko"), status, tiberoIncluded);

    // ---- DU1: IDriverPathSource ----

    [Fact]
    public async Task Path_source_returns_the_configured_tibero_path_trimmed()
    {
        var (settings, _) = await Create(@"  C:\drivers\Tibero.DataAccess.dll ");

        Assert.Equal(@"C:\drivers\Tibero.DataAccess.dll", new DriverPathSource(settings).GetPath(Dbms.Tibero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Path_source_returns_null_for_an_empty_setting(string? value)
    {
        var (settings, _) = await Create();
        await settings.UpdateAsync(s => s with { Drivers = new DriverSettings { TiberoPath = value } });

        Assert.Null(new DriverPathSource(settings).GetPath(Dbms.Tibero));
    }

    [Fact]
    public async Task Path_source_returns_null_for_oracle_even_when_a_tibero_path_is_set()
    {
        var (settings, _) = await Create(@"C:\drivers");

        Assert.Null(new DriverPathSource(settings).GetPath(Dbms.Oracle));
    }

    // ---- DU2: 상태 줄 ----

    [Theory]
    [InlineData(DriverState.Ok, "Tibero.DataAccess 1.1.35 — 사용 가능")]
    [InlineData(DriverState.Unverified, "1.1.35 — 시험하지 않은 버전이지만 사용할 수 있음")]
    [InlineData(DriverState.FrameworkBuild, ".NET Framework 용 TDP.NET(2.x)은 쓸 수 없음. TDP.NET Core 1.1.x 가 필요합니다")]
    [InlineData(DriverState.NotFound, "파일 없음")]
    [InlineData(DriverState.WrongAssembly, "Tibero 드라이버가 아님")]
    [InlineData(DriverState.Missing, "지정되지 않음")]
    public async Task Check_shows_the_status_line_for_each_result(DriverState state, string expected)
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(state, "1.1.35") };
        var page = Page(settings, status);

        page.Path = @"C:\drivers";
        page.CheckCommand.Execute(null);

        Assert.Equal(expected, page.StatusText);
        Assert.Equal(@"C:\drivers", status.LastChecked);
    }

    [Fact]
    public async Task Unusable_shows_the_missing_members_in_the_status_line_without_repeating_them()
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(DriverState.Unusable, "1.1.35", Reason: "없는 멤버: TiberoCommand.BindByName") };
        var page = Page(settings, status);

        page.CheckCommand.Execute(null);

        Assert.Equal("쓸 수 없음(없는 멤버: TiberoCommand.BindByName)", page.StatusText);
        Assert.False(page.HasStatusDetail);
        Assert.True(page.StatusIsError);
    }

    [Fact]
    public async Task Unusable_with_a_reason_that_already_says_so_is_shown_as_is_without_wrapping()
    {
        var (settings, _) = await Create();
        const string reason = "이 드라이버는 쓸 수 없습니다(없는 멤버: TiberoCommand.BindByName)";
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(DriverState.Unusable, "1.1.35", Reason: reason) };
        var page = Page(settings, status);

        page.CheckCommand.Execute(null);

        Assert.Equal(reason, page.StatusText);
        Assert.False(page.HasStatusDetail);
        Assert.True(page.StatusIsError);
    }

    [Fact]
    public async Task The_reason_is_shown_as_a_second_sentence()
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(DriverState.NotFound, Reason: "지정한 위치에 Tibero.DataAccess.dll 이 없습니다.") };
        var page = Page(settings, status);

        page.CheckCommand.Execute(null);

        Assert.Equal("파일 없음", page.StatusText);
        Assert.Equal("지정한 위치에 Tibero.DataAccess.dll 이 없습니다.", page.StatusDetail);
        Assert.True(page.StatusIsError);
    }

    [Fact]
    public async Task Restart_required_appends_the_restart_notice()
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(DriverState.Ok, "1.1.35"), RestartRequired = true };
        var page = Page(settings, status);

        page.CheckCommand.Execute(null);

        Assert.Equal("Tibero.DataAccess 1.1.35 — 사용 가능 — 다시 시작하면 적용", page.StatusText);
    }

    [Fact]
    public async Task The_page_opens_with_the_current_driver_state()
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { Current = new DriverCheck(DriverState.Ok, "1.1.35") };

        Assert.Equal("Tibero.DataAccess 1.1.35 — 사용 가능", Page(settings, status).StatusText);
        Assert.Equal(0, status.Checks); // 열 때는 검사하지 않는다
    }

    // ---- DU2: 로더 없음 ----

    [Theory]
    [InlineData(true, "드라이버 로더를 찾을 수 없음")]
    [InlineData(false, "이 빌드에는 Tibero 가 포함되어 있지 않음")]
    public async Task Without_a_loader_the_check_button_is_disabled_and_the_status_says_why(bool tiberoIncluded, string expected)
    {
        var (settings, _) = await Create();
        var page = Page(settings, status: null, tiberoIncluded);

        Assert.False(page.CanCheck);
        Assert.False(page.CheckCommand.CanExecute(null));
        Assert.Equal(expected, page.StatusText);
        Assert.True(page.StatusIsError);
    }

    // ---- DU2: 지우기·저장·찾아보기 ----

    [Fact]
    public async Task Clear_empties_the_field_and_saves_the_setting()
    {
        var (settings, store) = await Create(@"C:\drivers");
        var page = Page(settings, new FakeStatus());
        Assert.Equal(@"C:\drivers", page.Path);

        await page.ClearCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, page.Path);
        Assert.Null(store.Saved.Drivers.TiberoPath);
        Assert.Null(settings.Current.Drivers.TiberoPath);
        Assert.Equal("지정되지 않음", page.StatusText);
    }

    [Fact]
    public async Task Settings_save_writes_the_tibero_path_trimmed_and_leaves_other_sections()
    {
        var (settings, store) = await Create();
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), drivers: Page(settings, new FakeStatus()));
        vm.Drivers.Path = @"  D:\tdp\Tibero.DataAccess.dll  ";
        vm.AutoCommit = !vm.AutoCommit;

        await vm.SaveAsync();

        Assert.Equal(@"D:\tdp\Tibero.DataAccess.dll", store.Saved.Drivers.TiberoPath);
        Assert.False(vm.Drivers.IsDirty);
    }

    [Fact]
    public async Task Settings_save_keeps_the_stored_path_when_the_field_was_not_touched()
    {
        var (settings, store) = await Create(@"C:\drivers");
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), drivers: Page(settings, new FakeStatus()));
        await settings.UpdateAsync(s => s with { Drivers = new DriverSettings { TiberoPath = @"E:\other" } }); // 창이 열려 있는 사이 다른 곳이 바꿈

        await vm.SaveAsync();

        Assert.Equal(@"E:\other", store.Saved.Drivers.TiberoPath);
    }

    [Fact]
    public async Task Browse_fills_the_field_with_the_picked_path_and_checks_it()
    {
        var (settings, _) = await Create();
        var status = new FakeStatus { OnCheck = _ => new DriverCheck(DriverState.Ok, "1.1.35") };
        var page = Page(settings, status);
        page.PickFile = () => Task.FromResult<string?>(@"C:\pkg\tdp.nupkg");

        await page.BrowseFileCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\pkg\tdp.nupkg", page.Path);
        Assert.Equal(@"C:\pkg\tdp.nupkg", status.LastChecked);
        Assert.Equal("Tibero.DataAccess 1.1.35 — 사용 가능", page.StatusText);
    }

    [Fact]
    public async Task Cancelling_the_browse_dialog_changes_nothing()
    {
        var (settings, _) = await Create(@"C:\drivers");
        var status = new FakeStatus();
        var page = Page(settings, status);
        page.PickFolder = () => Task.FromResult<string?>(null);

        await page.BrowseFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\drivers", page.Path);
        Assert.Equal(0, status.Checks);
    }
}
