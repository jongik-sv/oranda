using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.AiSetup.Server;
using Oranda.App.Shell.Settings;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Ai;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Server;

/// <summary>B1 회사 AI 서버: 받은 값으로 프로필 저장 → 활성, 키는 키 저장소에만, 이름 중복, 주소만 받았을 때 모델 자동 선택.</summary>
public sealed class ServerProfileSaverTests
{
    private const string Url = "https://ai.example.com/v1";

    private static ServerDraft Draft(string? model = "company-sql-model", bool localAi = false, AiApiFormat format = AiApiFormat.OpenAi) =>
        new("회사 AI", Url, format, model, localAi);

    private static ServerProfileSaver Saver(ProfileHarness h) => new(h.Services, new ShellText(language: "ko"));

    [Fact]
    public async Task Save_CreatesProfile_MakesItActive_AndStoresKeyOnlyInStore()
    {
        var h = await ProfileHarness.CreateAsync();
        var saver = Saver(h);

        var result = await saver.SaveAsync(Draft(), "secret-key-123", default);

        Assert.True(result.Success, result.Error);
        var ai = h.Store.Saved.Ai;
        var profile = Assert.Single(ai.Profiles);
        Assert.Equal("회사 AI", profile.Name);
        Assert.Equal(Url, profile.BaseUrl);
        Assert.Equal(AiApiFormat.OpenAi, profile.Format);
        Assert.Equal("company-sql-model", profile.Model);
        Assert.Null(profile.LocalAiConfirmedUrl);
        Assert.Equal(profile.Id, ai.ActiveProfileId);
        Assert.Equal(AiDriverKind.ClaudeCode, ai.Driver);
        Assert.Null(ai.SystemCliId);
        Assert.Equal("secret-key-123", h.Credentials.Get(AiProfileCredentials.KeyFor(profile.Id)));
        Assert.DoesNotContain("secret-key-123", System.Text.Json.JsonSerializer.Serialize(h.Store.Saved));
    }

    [Fact]
    public async Task LocalAi_OnlyWhenDraftSaysSo()
    {
        var h = await ProfileHarness.CreateAsync();

        Assert.True((await Saver(h).SaveAsync(Draft(localAi: true), null, default)).Success);

        Assert.Equal(Url, Assert.Single(h.Store.Saved.Ai.Profiles).LocalAiConfirmedUrl);
    }

    [Fact]
    public async Task SameName_GetsNumberedName()
    {
        var existing = ProfileSamples.Example() with { Name = "회사 AI" };
        var h = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [existing] });

        Assert.True((await Saver(h).SaveAsync(Draft(), null, default)).Success);

        Assert.Equal(["회사 AI", "회사 AI (2)"], h.Store.Saved.Ai.Profiles.Select(p => p.Name));
    }

    [Fact]
    public async Task BadAddress_IsRefusedInPlainWords_AndNothingSaved()
    {
        var h = await ProfileHarness.CreateAsync();

        var result = await Saver(h).SaveAsync(Draft() with { BaseUrl = "ai.example.com" }, null, default);

        Assert.False(result.Success);
        Assert.Contains("주소", result.Error);
        Assert.Empty(h.Store.Saved.Ai.Profiles);
        Assert.Null(h.Store.Saved.Ai.ActiveProfileId);
    }

    [Fact]
    public async Task SettingsWriteFailure_ShowsError_AndRollsBackKey()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Store.FailSave = true;

        var result = await Saver(h).SaveAsync(Draft(), "secret-key-123", default);

        Assert.False(result.Success);
        Assert.Contains("저장하지 못했", result.Error);
        Assert.DoesNotContain("secret-key-123", result.Error);
        Assert.Empty(h.Credentials.Keys);
        Assert.Empty(h.Settings.Current.Ai.Profiles);
        Assert.Null(h.Settings.Current.Ai.ActiveProfileId);
    }

    [Fact]
    public async Task FindModels_PicksFirstModel_TryingOpenAiFormatFirst()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("model-a"), new AiModelInfo("model-b")]);

        var found = await Saver(h).FindModelsAsync(Url, "secret-key-123", preferred: null, default);

        Assert.True(found.Success, found.Error);
        Assert.Equal(AiApiFormat.OpenAi, found.Format);
        Assert.Equal(["model-a", "model-b"], found.Models);
        Assert.Equal("model-a", found.Chosen);
        Assert.Equal("secret-key-123", h.Probe.LastRequest!.ApiKey);
    }

    [Fact]
    public async Task FindModels_KeepsPreferredModel_WhenServerHasIt()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("model-a"), new AiModelInfo("company-sql-model")]);

        var found = await Saver(h).FindModelsAsync(Url, null, preferred: "company-sql-model", default);

        Assert.Equal("company-sql-model", found.Chosen);
    }

    [Fact]
    public async Task FindModels_Failure_ShowsPlainMessage()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Fail("401 unauthorized");

        var found = await Saver(h).FindModelsAsync(Url, null, preferred: null, default);

        Assert.False(found.Success);
        Assert.Contains("모델 목록을 받지 못했어요", found.Error);
        Assert.Equal(2, h.Probe.Calls); // 두 형식을 다 물어본 뒤 실패
    }

    /// <summary>N 번째 쓰기부터 실패하는 설정 저장소(활성 쓰기만 실패시키기).</summary>
    private sealed class FailAfterStore(int okSaves) : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = new();

        private int _saves;

        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Saved, false));

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            if (++_saves > okSaves)
            {
                throw new IOException("disk full (test)");
            }

            Saved = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>형식마다 다른 목록을 주는 서버 조회.</summary>
    private sealed class FormatProbe : IAiProfileProbe
    {
        public Dictionary<AiApiFormat, AiModelListResult> Results { get; } = [];

        public List<AiApiFormat> Asked { get; } = [];

        public Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct)
        {
            Asked.Add(request.Profile.Format);
            return Task.FromResult(Results.TryGetValue(request.Profile.Format, out var r) ? r : AiModelListResult.Fail("404 (test)"));
        }

        public Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<int?> ReadContextLengthAsync(AiProfileProbeRequest request, CancellationToken ct) => Task.FromResult<int?>(null);
    }

    [Fact]
    public async Task ActivateWriteFailure_KeepsProfileAndKey_RestoresSelection_AndRetryActivates()
    {
        var store = new FailAfterStore(okSaves: 2); // 1 = 앞 상태, 2 = 프로필, 3 = 활성(실패)
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        await settings.UpdateAsync(s => s with { Ai = s.Ai with { SystemCliId = "codex" } });
        var credentials = new TestCredentialStore();
        var saver = new ServerProfileSaver(new AiProfileServices(settings, credentials, new FakeProbe(), new FakeEventBus()), null);
        var before = settings.Current.Ai;

        var result = await saver.SaveAsync(Draft(), "secret-key-123", default);

        Assert.False(result.Success);
        Assert.NotNull(result.ProfileId);
        Assert.Contains("고르지 못했어요", result.Error);
        Assert.DoesNotContain("secret-key-123", result.Error);
        Assert.Single(settings.Current.Ai.Profiles);
        Assert.Equal(before.ActiveProfileId, settings.Current.Ai.ActiveProfileId);
        Assert.Equal(before.SystemCliId, settings.Current.Ai.SystemCliId);
        Assert.Single(credentials.Keys);

        var retry = await saver.ActivateAsync(result.ProfileId!.Value, default);
        Assert.False(retry.Success); // 저장소가 아직 막혀 있다
        Assert.Single(settings.Current.Ai.Profiles); // 다시 시도해도 프로필이 늘지 않는다
    }

    [Fact]
    public async Task Activate_ClearsSystemCliChoice()
    {
        var h = await ProfileHarness.CreateAsync(new AiSettings { SystemCliId = "codex" });

        Assert.True((await Saver(h).SaveAsync(Draft(), null, default)).Success);

        Assert.Null(h.Store.Saved.Ai.SystemCliId);
    }

    [Fact]
    public async Task KeyStoreFailure_SavesNothing()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Credentials.FailSave = true;

        var result = await Saver(h).SaveAsync(Draft(), "secret-key-123", default);

        Assert.False(result.Success);
        Assert.DoesNotContain("secret-key-123", result.Error);
        Assert.Empty(h.Settings.Current.Ai.Profiles);
        Assert.Null(h.Settings.Current.Ai.ActiveProfileId);
    }

    [Fact]
    public async Task KeyWithoutKeyStore_IsRefused()
    {
        var h = await ProfileHarness.CreateAsync(withCredentials: false);

        var result = await Saver(h).SaveAsync(Draft(), "secret-key-123", default);

        Assert.False(result.Success);
        Assert.Empty(h.Store.Saved.Ai.Profiles);
    }

    [Fact]
    public async Task UnknownFormat_IsRefused()
    {
        var h = await ProfileHarness.CreateAsync();

        Assert.False((await Saver(h).SaveAsync(Draft(format: AiApiFormat.Unknown), null, default)).Success);
        Assert.Empty(h.Store.Saved.Ai.Profiles);
    }

    [Fact]
    public async Task FindModels_FallsBackToAnthropicFormat()
    {
        var h = await ProfileHarness.CreateAsync();
        var probe = new FormatProbe();
        probe.Results[AiApiFormat.Anthropic] = AiModelListResult.Ok([new AiModelInfo("claude-x")]);
        var saver = new ServerProfileSaver(h.Services with { Probe = probe }, null);

        var found = await saver.FindModelsAsync(Url, null, null, default);

        Assert.True(found.Success);
        Assert.Equal(AiApiFormat.Anthropic, found.Format);
        Assert.Equal([AiApiFormat.OpenAi, AiApiFormat.Anthropic], probe.Asked);
    }

    [Fact]
    public async Task FindModels_EmptyList_SaysAskForModelName()
    {
        var h = await ProfileHarness.CreateAsync();
        h.Probe.ModelResult = AiModelListResult.Ok([]);

        var found = await Saver(h).FindModelsAsync(Url, null, null, default);

        Assert.False(found.Success);
        Assert.Contains("모델 이름을 물어보세요", found.Error);
    }

    [Fact]
    public async Task FindModels_BadAddress_NeverAsksServer()
    {
        var h = await ProfileHarness.CreateAsync();

        var found = await Saver(h).FindModelsAsync("ai.example.com", "secret-key-123", null, default);

        Assert.False(found.Success);
        Assert.Equal(0, h.Probe.Calls);
    }

    [Fact]
    public void PlainHttpRemote_IsFlagged()
    {
        Assert.True(ServerProfileSaver.IsPlainText("http://ai.example.com/v1"));
        Assert.False(ServerProfileSaver.IsPlainText(Url));
        Assert.False(ServerProfileSaver.IsPlainText("http://localhost:11434/v1"));
    }
}
