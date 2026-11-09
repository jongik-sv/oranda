using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>접속 대화 상자의 DBMS 선택(A1, D-046): Tibero 는 Oranda.Tibero 어셈블리가 있을 때만 보이고, 고르면 포트·접속 방식이 따라 맞춰진다.</summary>
public sealed class ProfileEditorDbmsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oranda-dbms-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly Dbms[] OracleOnly = [Dbms.Oracle];
    private static readonly Dbms[] Both = [Dbms.Oracle, Dbms.Tibero];

    [Fact]
    public void Tibero_is_listed_only_when_the_provider_assembly_is_next_to_the_app()
    {
        Assert.Equal([Dbms.Oracle], DbmsAvailability.Detect(_dir));

        File.WriteAllBytes(Path.Combine(_dir, "Oranda.Tibero.dll"), []);

        Assert.Equal([Dbms.Oracle, Dbms.Tibero], DbmsAvailability.Detect(_dir));
    }

    [Fact]
    public void Tibero_is_listed_only_when_the_selector_has_its_session_manager_too()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Oranda.Tibero.dll"), []);
        var withoutSessions = new DbmsServiceTable().Add<IConnectionProbe>(Dbms.Tibero, () => new FakeConnectionProbe(new StubConnectionTester(), new StubPasswordChanger()) { Dbms = Dbms.Tibero });
        var withSessions = new DbmsServiceTable().Add<ISessionManager>(Dbms.Tibero, () => new StubSessionManager(new EventBus()));

        Assert.Equal([Dbms.Oracle], DbmsAvailability.Detect(_dir, null)); // 선택기 없음
        Assert.Equal([Dbms.Oracle], DbmsAvailability.Detect(_dir, withoutSessions)); // 어셈블리만 있고 세션 관리자는 아직 없음
        Assert.Equal([Dbms.Oracle, Dbms.Tibero], DbmsAvailability.Detect(_dir, withSessions));
        Assert.Equal([Dbms.Oracle], DbmsAvailability.Detect(Path.Combine(_dir, "none"), withSessions)); // 세션 관리자가 있어도 어셈블리가 없으면 숨김
    }

    [Fact]
    public void Connection_service_offers_only_dbms_the_selector_can_open()
    {
        using var fx = new ServiceFixture();

        Assert.Equal([Dbms.Oracle], fx.Service.AvailableDbms);
    }

    [Fact]
    public void Dbms_choice_is_hidden_with_a_single_dbms_and_shown_with_two()
    {
        Assert.False(new ProfileEditorViewModel(OracleOnly).ShowDbmsChoice);

        var vm = new ProfileEditorViewModel(Both);

        Assert.True(vm.ShowDbmsChoice);
        Assert.Equal(["Oracle", "Tibero"], vm.DbmsChoices.Select(c => c.Label));
    }

    [Fact]
    public void Existing_oracle_profile_stays_oracle_and_saves_as_oracle()
    {
        var vm = ProfileEditorViewModel.From(new ConnectionProfile("dev", "scott", "db.example.com:1521/svc"));

        Assert.Equal(Dbms.Oracle, vm.Dbms);
        Assert.True(vm.ShowModeChoice);
        var saved = vm.TryBuild(out var errors);
        Assert.Empty(errors);
        Assert.Equal(Dbms.Oracle, saved!.Dbms);
    }

    [Fact]
    public void Choosing_tibero_switches_default_port_and_hides_oracle_only_fields()
    {
        var vm = new ProfileEditorViewModel(Both) { Name = "tb", User = "sys", Host = "tbhost", Service = "tb6", Mode = ConnectionMode.TnsAlias, UseTls = true };

        vm.Dbms = Dbms.Tibero;

        Assert.Equal("8629", vm.Port);
        Assert.Equal(ConnectionMode.HostPortService, vm.Mode);
        Assert.False(vm.UseTls);
        Assert.False(vm.ShowModeChoice);
        Assert.False(vm.ShowServiceKind);
        Assert.False(vm.UsesWalletFolder);
        var saved = vm.TryBuild(out var errors);
        Assert.Empty(errors);
        Assert.Equal(Dbms.Tibero, saved!.Dbms);

        vm.Dbms = Dbms.Oracle;

        Assert.Equal("1521", vm.Port);
        Assert.True(vm.ShowModeChoice);
    }

    [Fact]
    public void Switching_dbms_keeps_a_port_the_user_typed()
    {
        var vm = new ProfileEditorViewModel(Both) { Port = "9999" };

        vm.Dbms = Dbms.Tibero;

        Assert.Equal("9999", vm.Port);
    }

    [Fact]
    public void Saved_tibero_profile_opens_as_tibero_even_without_the_provider_assembly()
    {
        var profile = new ConnectionProfile("tb", "sys", "tbhost:8629/tb6", Dbms: Dbms.Tibero);

        var vm = ProfileEditorViewModel.From(profile);

        Assert.Equal(Dbms.Tibero, vm.Dbms);
        Assert.Contains(vm.DbmsChoices, c => c.Value == Dbms.Tibero);
        Assert.False(vm.IsDirty);
        Assert.Equal("8629", vm.Port);
    }

    [Fact]
    public void Changing_dbms_marks_the_form_dirty()
    {
        var vm = new ProfileEditorViewModel(Both);
        Assert.False(vm.IsDirty);

        vm.Dbms = Dbms.Tibero;

        Assert.True(vm.IsDirty);
    }
}
