using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Shell;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.AiSetup.Access;

/// <summary>B2 「AI 를 쓸 접속」: 켬/끔 스위치 → AI 허용 수준 저장(켬 = 환경 기본값 null, 끔 = Off), 운영 빨간 표시, 저장 실패 글.</summary>
public sealed class AccessStepTests
{
    private static ConnectionProfile Profile(string name, ConnectionEnvironment environment, AiAccessLevel? level = null) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment, AiAccess: level);

    private sealed class Harness
    {
        public List<ConnectionProfile> Profiles { get; } = [];

        public List<ConnectionProfile> Saved { get; } = [];

        public Exception? SaveFailure { get; set; }

        public Exception? LoadFailure { get; set; }

        public AccessStepViewModel Create(string language = "ko") => new(
            new AccessStepServices(
                _ => LoadFailure is { } f ? Task.FromException<IReadOnlyList<ConnectionProfile>>(f) : Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles),
                (p, _) =>
                {
                    if (SaveFailure is { } failure)
                    {
                        throw failure;
                    }

                    Saved.Add(p);
                    return Task.FromResult(p);
                }),
            new ShellText(language: language));
    }

    [Fact]
    public async Task Load_ShowsOneRowPerConnection_WithSwitchFromEffectiveLevel()
    {
        var h = new Harness();
        h.Profiles.AddRange([Profile("dev-erp", ConnectionEnvironment.Development), Profile("prd-off", ConnectionEnvironment.Production, AiAccessLevel.Off)]);
        var vm = h.Create();

        await vm.LoadAsync(default);

        Assert.Equal(["dev-erp", "prd-off"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].IsOn);
        Assert.False(vm.Rows[1].IsOn);
        Assert.Equal("개발", vm.Rows[0].EnvironmentLabel);
        Assert.True(vm.Rows[1].IsProduction);
    }

    [Fact]
    public async Task TurnOnDev_FromOff_SavesDefault_AndPassesEnabledList()
    {
        var h = new Harness();
        var dev = Profile("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.Off);
        h.Profiles.Add(dev);
        var vm = h.Create();
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = true;
        Assert.True(await vm.SaveAsync(default));

        var saved = Assert.Single(h.Saved);
        Assert.Null(saved.AiAccess);
        Assert.Equal(AiAccessLevel.Plan, AiAccessPolicy.Effective(saved));
        Assert.Equal([dev.Id], vm.EnabledProfiles.Select(p => p.Id));
        Assert.True(vm.IsComplete);
    }

    [Fact]
    public async Task TurnOff_SavesOff()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("test-erp", ConnectionEnvironment.Test));
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development));
        var vm = h.Create();
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = false;
        Assert.True(await vm.SaveAsync(default));

        var saved = Assert.Single(h.Saved); // 바꾸지 않은 줄은 저장하지 않는다
        Assert.Equal(AiAccessLevel.Off, saved.AiAccess);
        Assert.Equal(["dev-erp"], vm.EnabledProfiles.Select(p => p.Name));
    }

    [Fact]
    public async Task Production_On_KeepsDefault_RedMark_And_NeverNeedsNameConfirmation()
    {
        var h = new Harness();
        var before = Profile("prd-erp", ConnectionEnvironment.Production, AiAccessLevel.Off);
        h.Profiles.Add(before);
        var vm = h.Create();
        await vm.LoadAsync(default);
        var row = vm.Rows[0];

        row.IsOn = true;
        Assert.True(row.IsProduction);
        Assert.Contains("가려서", row.Description);
        Assert.Contains("행 값은 보내지 않아요", row.Description);
        Assert.True(await vm.SaveAsync(default));

        var saved = Assert.Single(h.Saved);
        Assert.Null(saved.AiAccess);
        Assert.Equal(AiAccessLevel.Query, AiAccessPolicy.Effective(saved));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(before, saved));
    }

    [Fact]
    public async Task AllOff_CompletesWithSkipNotice()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development));
        var vm = h.Create();
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = false;
        Assert.True(vm.ShowSkipNotice);
        Assert.True(await vm.SaveAsync(default));

        Assert.True(vm.IsComplete);
        Assert.True(vm.TryItSkipped);
        Assert.Empty(vm.EnabledProfiles);
    }

    [Fact]
    public async Task SaveFailure_ShowsError_AndStepStaysOpen()
    {
        var h = new Harness { SaveFailure = new IOException("disk full (test)") };
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.Off));
        var vm = h.Create();
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = true;
        Assert.False(await vm.SaveAsync(default));

        Assert.False(vm.IsComplete);
        Assert.Contains("저장하지 못했어요", vm.ErrorText);
    }

    [Fact]
    public async Task NoConnections_ExplainsAndCompletesAsSkipped()
    {
        var h = new Harness();
        var vm = h.Create();
        await vm.LoadAsync(default);

        Assert.True(vm.HasNoConnections);
        Assert.True(await vm.SaveAsync(default));
        Assert.True(vm.TryItSkipped);
    }

    [Fact]
    public async Task LoadFailure_ShowsError()
    {
        var h = new Harness { LoadFailure = new IOException("broken (test)") };
        var vm = h.Create("en");
        await vm.LoadAsync(default);

        Assert.Empty(vm.Rows);
        Assert.Contains("Could not read", vm.ErrorText);
    }

    [Fact]
    public async Task EnabledList_PutsProductionLast()
    {
        var h = new Harness();
        h.Profiles.AddRange([Profile("prd-erp", ConnectionEnvironment.Production), Profile("dev-erp", ConnectionEnvironment.Development), Profile("test-erp", ConnectionEnvironment.Test)]);
        var vm = h.Create();
        await vm.LoadAsync(default);

        Assert.True(await vm.SaveAsync(default));

        Assert.Equal(["dev-erp", "test-erp", "prd-erp"], vm.EnabledProfiles.Select(p => p.Name));
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task ExplicitLevel_OnUntouchedRow_IsKept()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.QueryRows));
        var vm = h.Create();
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = false;
        vm.Rows[0].IsOn = true; // 껐다 다시 켜면 바뀐 것이 없다
        Assert.True(await vm.SaveAsync(default));

        Assert.Empty(h.Saved);
        Assert.Equal(AiAccessLevel.QueryRows, vm.EnabledProfiles.Single().AiAccess);
    }

    [Fact]
    public async Task RetryAfterPartialFailure_SavesOnlyTheRest()
    {
        var h = new Harness();
        h.Profiles.AddRange([Profile("dev-a", ConnectionEnvironment.Development), Profile("dev-b", ConnectionEnvironment.Development)]);
        var calls = 0;
        var flaky = new AccessStepViewModel(
            new AccessStepServices(
                _ => Task.FromResult<IReadOnlyList<ConnectionProfile>>(h.Profiles),
                (p, _) => ++calls == 2 ? throw new IOException("busy (test)") : Task.FromResult(p)),
            new ShellText(language: "ko"));
        await flaky.LoadAsync(default);
        flaky.Rows[0].IsOn = false;
        flaky.Rows[1].IsOn = false;

        Assert.False(await flaky.SaveAsync(default));
        Assert.Contains("dev-b", flaky.ErrorText);
        Assert.True(await flaky.SaveAsync(default));

        Assert.Equal(3, calls);
        Assert.Empty(flaky.ErrorText);
        Assert.True(flaky.TryItSkipped);
    }

    [Fact]
    public async Task SwitchAfterSave_ReopensStep()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development));
        var vm = h.Create();
        await vm.LoadAsync(default);
        Assert.True(await vm.SaveAsync(default));

        vm.Rows[0].IsOn = false;

        Assert.False(vm.IsComplete);
    }

    [Fact]
    public async Task LanguageSwitch_RebuildsLabelsAndError()
    {
        var h = new Harness { SaveFailure = new IOException("disk full (test)") };
        h.Profiles.Add(Profile("prd-erp", ConnectionEnvironment.Production, AiAccessLevel.Off));
        var text = new ShellText(language: "ko");
        var vm = new AccessStepViewModel(
            new AccessStepServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>(h.Profiles), (_, _) => throw new IOException("disk full (test)")),
            text);
        await vm.LoadAsync(default);
        vm.Rows[0].IsOn = true;
        Assert.False(await vm.SaveAsync(default));

        text.SetLanguage("en");

        Assert.Equal("Production", vm.Rows[0].EnvironmentLabel);
        Assert.Contains("never sent", vm.Rows[0].Description);
        Assert.StartsWith("Could not save", vm.ErrorText);
        Assert.Equal("Connections that use AI", vm.Title);
    }

    [Fact]
    public async Task FromConnections_SavesWithoutPassword_KeepingStoredOne()
    {
        var service = new FakeConnections();
        var withStored = Profile("dev-erp", ConnectionEnvironment.Development) with { CredentialRef = "ref-1" };
        var parts = AccessStepServices.FromConnections(service);

        await parts.Save(withStored, default);
        await parts.Save(withStored with { CredentialRef = null }, default);

        Assert.Equal([(null, true), (null, false)], service.Calls);
    }

    [Fact]
    public async Task AutoSave_SavesEachSwitchAtOnce()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("dev-erp", ConnectionEnvironment.Development));
        var vm = new AccessStepViewModel(
            new AccessStepServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>(h.Profiles), (p, _) => { h.Saved.Add(p); return Task.FromResult(p); }),
            new ShellText(language: "ko")) { AutoSave = true };
        await vm.LoadAsync(default);

        vm.Rows[0].IsOn = false;
        await vm.LastSave;

        Assert.Equal(AiAccessLevel.Off, Assert.Single(h.Saved).AiAccess);
        Assert.True(vm.IsComplete);
    }
}
