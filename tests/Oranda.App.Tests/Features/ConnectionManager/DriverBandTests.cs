using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>시험용 <see cref="IDbmsDriverStatus"/>: <see cref="Current"/> 를 바꿔 설정에서 경로를 바꾼 상황을 흉내 낸다.</summary>
internal sealed class FakeDriverStatus(DriverCheck current) : IDbmsDriverStatus
{
    public Dbms Dbms => Dbms.Tibero;

    public DriverCheck Current { get; set; } = current;

    public string? ConfiguredPath => Current.Path;

    public bool RestartRequired => false;

    public DriverCheck Check(string? path) => Current;
}

// Tibero 드라이버 지정 DU3: 접속 대화상자의 드라이버 상태 띠와 [접속]·[시험] 비활성. 서비스·저장소는 ConnectionManagerTests.cs 의 ServiceFixture.
// 글자(UiText)는 전역이라 앱 정적 상태 묶음에서 한국어로 고정해 돈다.
[Collection("GridEditStatics")]
public sealed class DriverBandTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public DriverBandTests() => UiText.Current = new ShellText(language: "ko");

    public void Dispose() => UiText.Current = _previousText;

    private static readonly DriverCheck Ok = new(DriverState.Ok, "1.1.35");

    /// <summary>Tibero 프로필 하나와 Oracle 프로필 하나를 저장한 접속 관리자. <paramref name="status"/> 가 null 이면 선택기에 드라이버 상태 구현이 없다.</summary>
    private static async Task<(ConnectionManagerViewModel Vm, Guid Tibero, Guid Oracle)> OpenAsync(ServiceFixture fx, FakeDriverStatus? status)
    {
        if (status is not null)
        {
            fx.Selector.Add<IDbmsDriverStatus>(Dbms.Tibero, () => status);
        }

        var tibero = await fx.Service.SaveProfileAsync(new ConnectionProfile("tb", "sys", "localhost:8629/tb", Dbms: Dbms.Tibero), "pw", true, CancellationToken.None);
        var oracle = await fx.Service.SaveProfileAsync(new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        return (vm, tibero.Id, oracle.Id);
    }

    private static void Select(ConnectionManagerViewModel vm, Guid id) => vm.Selected = vm.AllNodes().First(n => n.Profile?.Id == id);

    // ---------- (a) Tibero 프로필 + 쓸 수 없는 드라이버: 띠·[접속]·[시험] 꺼짐 + 이유 ----------

    [Fact]
    public async Task Missing_driver_shows_the_band_and_turns_connect_and_test_off_with_the_reason()
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, new FakeDriverStatus(DriverCheck.Missing));

        Select(vm, tibero);

        var editor = vm.Editor!;
        Assert.True(editor.ShowDriverBand);
        Assert.Equal("Tibero 드라이버가 지정되지 않았습니다.", editor.DriverBandText);
        Assert.Equal("드라이버 지정…", editor.DriverBandButtonText);
        Assert.Equal(editor.DriverBandText, editor.DriverBlockReason);
        Assert.True(editor.HasRequiredFields);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.TestCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(DriverState.NotFound, null, "Tibero 드라이버 파일을 찾을 수 없습니다.")]
    [InlineData(DriverState.WrongAssembly, "Tibero 드라이버가 아닌 파일입니다.", "쓸 수 없는 Tibero 드라이버입니다: Tibero 드라이버가 아닌 파일입니다.")]
    [InlineData(DriverState.FrameworkBuild, ".NET Framework 용 2.70 은 쓸 수 없습니다.", "쓸 수 없는 Tibero 드라이버입니다: .NET Framework 용 2.70 은 쓸 수 없습니다.")]
    [InlineData(DriverState.Unusable, "없는 멤버: TiberoCommand.BindByName", "쓸 수 없는 Tibero 드라이버입니다: 없는 멤버: TiberoCommand.BindByName")]
    [InlineData(DriverState.Unusable, null, "쓸 수 없는 Tibero 드라이버입니다.")]
    public async Task Each_unusable_state_has_its_own_band_text_and_blocks_connecting(DriverState state, string? reason, string expected)
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, new FakeDriverStatus(new DriverCheck(state, Reason: reason)));

        Select(vm, tibero);

        Assert.Equal(expected, vm.Editor!.DriverBandText);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.TestCommand.CanExecute(null));
    }

    // ---------- (b) 쓸 수 있는 드라이버: 띠 없음·활성 ----------

    [Theory]
    [InlineData(DriverState.Ok)]
    [InlineData(DriverState.Unverified)]
    public async Task Usable_driver_has_no_band_and_leaves_connect_and_test_on(DriverState state)
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, new FakeDriverStatus(new DriverCheck(state, "1.1.40")));

        Select(vm, tibero);

        var editor = vm.Editor!;
        Assert.False(editor.ShowDriverBand);
        Assert.Equal(string.Empty, editor.DriverBandText);
        Assert.Null(editor.DriverBlockReason);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.TestCommand.CanExecute(null));
    }

    // ---------- (c) Oracle 프로필: 띠 없음, Tibero 로 바꾸면 걸리고 Oracle 로 돌리면 즉시 풀림 ----------

    [Fact]
    public async Task Oracle_profile_has_no_band_even_when_the_tibero_driver_is_missing()
    {
        using var fx = new ServiceFixture();
        var (vm, _, oracle) = await OpenAsync(fx, new FakeDriverStatus(DriverCheck.Missing));

        Select(vm, oracle);

        Assert.False(vm.Editor!.ShowDriverBand);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.TestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Switching_the_profile_dbms_to_oracle_clears_the_band_at_once_and_switching_back_restores_it()
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, new FakeDriverStatus(DriverCheck.Missing));
        Select(vm, tibero);
        Assert.False(vm.ConnectCommand.CanExecute(null));

        vm.Editor!.Dbms = Dbms.Oracle;

        Assert.False(vm.Editor.ShowDriverBand);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.TestCommand.CanExecute(null));

        vm.Editor.Dbms = Dbms.Tibero;

        Assert.True(vm.Editor.ShowDriverBand);
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // ---------- (d) 구현 없음(null): 「지정되지 않음」 취급 ----------

    [Fact]
    public async Task A_tibero_profile_without_any_driver_status_implementation_is_treated_as_not_specified()
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, status: null);

        Select(vm, tibero);

        Assert.Equal("Tibero 드라이버가 지정되지 않았습니다.", vm.Editor!.DriverBandText);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.TestCommand.CanExecute(null));
        Assert.Equal("tb", Assert.Single(vm.AllNodes(), n => n.Profile?.Id == tibero).Profile!.Name); // 저장된 Tibero 프로필은 목록에 그대로 있다
    }

    // ---------- (e) [드라이버 지정…] = oranda.settings.openDrivers ----------

    [Fact]
    public async Task Assign_driver_button_runs_the_open_drivers_command()
    {
        using var fx = new ServiceFixture();
        var (vm, tibero, _) = await OpenAsync(fx, new FakeDriverStatus(DriverCheck.Missing));
        Select(vm, tibero);
        var ran = new List<string>();
        vm.RunCommand = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };

        await vm.OpenDriversCommand.ExecuteAsync(null);

        Assert.Equal(["oranda.settings.openDrivers"], ran);
        Assert.Equal(DriverSettingsCommands.OpenDrivers, ran[0]);
    }

    // ---------- 설정에서 경로를 바꾸고 돌아오면 다시 읽는다 ----------

    [Fact]
    public async Task Refreshing_after_the_path_was_fixed_in_settings_lifts_the_band_and_the_block()
    {
        using var fx = new ServiceFixture();
        var status = new FakeDriverStatus(DriverCheck.Missing);
        var (vm, tibero, _) = await OpenAsync(fx, status);
        Select(vm, tibero);
        Assert.False(vm.ConnectCommand.CanExecute(null));

        status.Current = Ok;
        vm.RefreshDriverStatus();

        Assert.False(vm.Editor!.ShowDriverBand);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.TestCommand.CanExecute(null));

        status.Current = new DriverCheck(DriverState.NotFound);
        vm.RefreshDriverStatus();

        Assert.Equal("Tibero 드라이버 파일을 찾을 수 없습니다.", vm.Editor.DriverBandText);
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }
}
