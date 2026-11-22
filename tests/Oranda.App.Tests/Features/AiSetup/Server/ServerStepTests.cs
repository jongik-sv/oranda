using Oranda.App.Features.AiSetup.Server;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Ai;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.AiSetup.Server;

/// <summary>B1 회사 AI 서버 단계 화면 모델: .oranda-ai 파일 → 프로필·활성, 키 칸이 든 파일 거절, 잘못된 주소, 주소만 → 모델 자동 선택·목록 실패 글, 로컬 AI.</summary>
public sealed class ServerStepTests
{
    private const string Url = "https://ai.example.com/v1";

    private static string File(string extra = "", string url = Url, string? model = "big-model") =>
        "{ \"format\": \"oranda-ai\", \"version\": 1, \"name\": \"사내 AI\", \"baseUrl\": \"" + url + "\", \"apiFormat\": \"openai\""
        + (model is null ? "" : ", \"model\": \"" + model + "\"") + extra + " }";

    private static async Task<(ProfileHarness H, ServerStepViewModel Vm)> CreateAsync(string? key = null)
    {
        var h = await ProfileHarness.CreateAsync();
        var vm = new ServerStepViewModel(new ServerProfileSaver(h.Services, null), new ShellText(language: "ko")) { KeyProvider = () => key };
        return (h, vm);
    }

    [Fact]
    public async Task GoodFile_CreatesActiveProfile_AndCompletes()
    {
        var (h, vm) = await CreateAsync("secret-key-123");

        vm.LoadFileText(File());
        Assert.True(vm.FileLoaded);
        Assert.Contains("사내 AI", vm.FileSummary);
        Assert.Contains("ai.example.com", vm.FileSummary);
        await vm.ConnectAsync();

        Assert.True(vm.IsComplete, vm.ErrorText);
        var profile = Assert.Single(h.Store.Saved.Ai.Profiles);
        Assert.Equal(profile.Id, h.Store.Saved.Ai.ActiveProfileId);
        Assert.Equal(profile.Id, vm.CreatedProfileId);
        Assert.Equal("big-model", profile.Model);
        Assert.Null(profile.LocalAiConfirmedUrl); // 파일에 localAi 가 없다
        Assert.Equal("secret-key-123", h.Credentials.Get(AiProfileCredentials.KeyFor(profile.Id)));
    }

    [Fact]
    public async Task FileWithLocalAiTrue_TurnsLocalAiOn()
    {
        var (h, vm) = await CreateAsync();

        vm.LoadFileText(File(", \"localAi\": true"));
        await vm.ConnectAsync();

        Assert.Equal(Url, Assert.Single(h.Store.Saved.Ai.Profiles).LocalAiConfirmedUrl);
    }

    [Fact]
    public async Task FileWithKeyField_IsRefused()
    {
        var (h, vm) = await CreateAsync();

        vm.LoadFileText(File(", \"apiKey\": \"sk-should-not-be-here\""));

        Assert.False(vm.FileLoaded);
        Assert.NotEmpty(vm.ErrorText);
        Assert.DoesNotContain("sk-should-not-be-here", vm.ErrorText);
        await vm.ConnectAsync();
        Assert.Empty(h.Store.Saved.Ai.Profiles);
    }

    [Fact]
    public async Task FileWithBadUrl_IsRefused()
    {
        var (_, vm) = await CreateAsync();

        vm.LoadFileText(File(url: "ftp://ai.example.com"));

        Assert.False(vm.FileLoaded);
        Assert.NotEmpty(vm.ErrorText);
    }

    [Fact]
    public async Task FileWithoutModel_AsksServerForModel()
    {
        var (h, vm) = await CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("auto-model")]);

        vm.LoadFileText(File(model: null));
        await vm.ConnectAsync();

        Assert.True(vm.IsComplete, vm.ErrorText);
        Assert.Equal("auto-model", Assert.Single(h.Store.Saved.Ai.Profiles).Model);
    }

    [Fact]
    public async Task AddressOnly_RefreshPicksModel_ThenConnect()
    {
        var (h, vm) = await CreateAsync("secret-key-123");
        h.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("model-a"), new AiModelInfo("model-b")]);

        vm.UseAddressOnly();
        Assert.True(vm.IsAddressMode);
        vm.Address = Url;
        await vm.RefreshModelsAsync();

        Assert.Equal(["model-a", "model-b"], vm.Models);
        Assert.Equal("model-a", vm.SelectedModel);
        await vm.ConnectAsync();

        Assert.True(vm.IsComplete, vm.ErrorText);
        var profile = Assert.Single(h.Store.Saved.Ai.Profiles);
        Assert.Equal("ai.example.com", profile.Name);
        Assert.Equal("model-a", profile.Model);
        Assert.Equal(AiApiFormat.OpenAi, profile.Format);
        Assert.Null(profile.LocalAiConfirmedUrl);
    }

    [Fact]
    public async Task AddressOnly_ListFailure_ShowsMessage()
    {
        var (h, vm) = await CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Fail("connection refused");

        vm.UseAddressOnly();
        vm.Address = Url;
        await vm.RefreshModelsAsync();

        Assert.Empty(vm.Models);
        Assert.Contains("모델 목록을 받지 못했어요", vm.ErrorText);
        Assert.False(vm.CanConnect);
    }

    [Fact]
    public async Task AddressOnly_BadAddress_ShowsPlainMessage()
    {
        var (h, vm) = await CreateAsync();

        vm.UseAddressOnly();
        vm.Address = "ai.example.com";
        await vm.RefreshModelsAsync();

        Assert.Contains("주소가 올바르지 않아요", vm.ErrorText);
        Assert.Equal(0, h.Probe.Calls);
    }

    [Fact]
    public async Task PlainHttpAddress_ShowsWarning()
    {
        var (_, vm) = await CreateAsync();

        vm.UseAddressOnly();
        vm.Address = "http://ai.example.com/v1";

        Assert.True(vm.ShowPlainTextWarning);
    }

    [Fact]
    public async Task Enter_WithExistingCreatedProfile_IsAlreadyComplete()
    {
        var existing = ProfileSamples.Example();
        var h = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [existing], ActiveProfileId = existing.Id });
        var vm = new ServerStepViewModel(new ServerProfileSaver(h.Services, null), null);

        vm.Restore(existing.Id, h.Settings.Current.Ai);

        Assert.True(vm.IsComplete);
    }

    [Fact]
    public async Task ConnectTwice_DoesNotCreateSecondProfile()
    {
        var (h, vm) = await CreateAsync("secret-key-123");
        vm.LoadFileText(File());

        await vm.ConnectAsync();
        Assert.False(vm.CanConnect);
        await vm.ConnectAsync();

        Assert.Single(h.Store.Saved.Ai.Profiles);
    }

    [Fact]
    public async Task AddressChange_DropsModelsFoundForOldAddress()
    {
        var (h, vm) = await CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("model-a")]);
        vm.UseAddressOnly();
        vm.Address = Url;
        await vm.RefreshModelsAsync();
        Assert.True(vm.CanConnect);

        vm.Address = "https://other.example.com/v1";

        Assert.Empty(vm.Models);
        Assert.Null(vm.SelectedModel);
        Assert.False(vm.CanConnect);
    }

    [Fact]
    public async Task ProbeThrowing_ShowsMessage_InsteadOfCrashing()
    {
        var h = await ProfileHarness.CreateAsync();
        var vm = new ServerStepViewModel(new ServerProfileSaver(h.Services with { Probe = new ThrowingProbe() }, null), null);
        vm.UseAddressOnly();
        vm.Address = Url;

        await vm.RefreshModelsAsync();

        Assert.NotEmpty(vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    private sealed class ThrowingProbe : IAiProfileProbe
    {
        public Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct) => throw new InvalidOperationException("boom (test)");

        public Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<int?> ReadContextLengthAsync(AiProfileProbeRequest request, CancellationToken ct) => Task.FromResult<int?>(null);
    }
}
