using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai;

/// <summary>
/// 실제 <see cref="SettingsStore"/> 와 파일로 확인하는 프로필 칸의 읽기:
/// 모르는 프리셋·형식·깨진 활성 id 가 설정 전체를 초기화하지 않고, 손상 복구만 한 번 신호로 남는다.
/// </summary>
public sealed class AiProfileSettingsFileTests : IDisposable
{
    private const string ProfileId = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-aiprofile-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEventBus _bus = new();

    public AiProfileSettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private AppDataPaths Paths => new(_dir);

    private SettingsStore NewStore() => new(Paths, _bus, []);

    private Task WriteSettingsAsync(string json) => File.WriteAllTextAsync(Paths.SettingsFile, json);

    [Fact]
    public async Task Unknown_preset_format_and_a_broken_active_id_do_not_reset_the_whole_file()
    {
        await WriteSettingsAsync($$"""
            {
              "schemaVersion": 1,
              "ui": { "theme": "Dark" },
              "ai": {
                "driver": "ClaudeCode",
                "activeProfileId": "",
                "profiles": [
                  { "id": "{{ProfileId}}", "name": "미래", "preset": "DeepSeek", "format": "Responses", "baseUrl": "http://llm.example:8080", "model": "m" }
                ]
              }
            }
            """);
        var store = NewStore();

        var doc = await store.LoadAsync(default);

        Assert.Equal(Theme.Dark, doc.Value.Ui.Theme);
        Assert.Null(doc.Notice);
        Assert.False(store.TryTakeReset());
        var profile = Assert.Single(doc.Value.Ai.Profiles);
        Assert.Equal((AiProfilePreset.Unknown, AiApiFormat.Unknown), (profile.Preset, profile.Format));
        Assert.Equal(Guid.Empty, doc.Value.Ai.ActiveProfileId);
        Assert.False(Directory.Exists(Paths.BackupDirectory) && Directory.GetFiles(Paths.BackupDirectory).Length > 0);
    }

    [Fact]
    public async Task A_profile_element_with_missing_or_null_fields_gets_defaults()
    {
        await WriteSettingsAsync($$"""
            { "schemaVersion": 1, "ai": { "profiles": [ { "id": "{{ProfileId}}", "name": "x", "baseUrl": null, "model": null, "maxInputTokens": 0, "maxOutputTokens": -5 } ] } }
            """);

        var profile = Assert.Single((await NewStore().LoadAsync(default)).Value.Ai.Profiles);

        Assert.Equal(("x", "", ""), (profile.Name, profile.BaseUrl, profile.Model));
        Assert.Equal((AiLlmProfile.DefaultMaxInputTokens, AiLlmProfile.DefaultMaxOutputTokens), (profile.MaxInputTokens, profile.MaxOutputTokens));
    }

    [Fact]
    public async Task A_corrupt_file_leaves_exactly_one_reset_signal()
    {
        await WriteSettingsAsync("{ 이건 json 이 아니다");
        var store = NewStore();

        var doc = await store.LoadAsync(default);

        Assert.NotNull(doc.Notice);
        Assert.True(store.TryTakeReset());
        Assert.False(store.TryTakeReset());

        await store.LoadAsync(default); // 손상 파일은 이미 옮겨졌으므로 이어지는 읽기는 신호를 만들지 않는다
        Assert.False(store.TryTakeReset());
    }

    [Fact]
    public async Task A_corrupt_file_seen_first_by_a_save_also_leaves_the_signal()
    {
        await WriteSettingsAsync("{ 이건 json 이 아니다");
        var store = NewStore();

        await store.SaveAsync(new AppSettings(), default);

        Assert.True(store.TryTakeReset());
    }

    [Fact]
    public async Task A_corrupt_file_that_cannot_be_moved_raises_the_signal_only_once()
    {
        await WriteSettingsAsync("{ 이건 json 이 아니다");
        await File.WriteAllTextAsync(Paths.BackupDirectory, "백업 폴더 자리에 파일이 있어 폴더를 만들 수 없다");
        var store = NewStore();

        await store.LoadAsync(default);
        Assert.True(store.TryTakeReset());

        await store.LoadAsync(default); // 같은 손상이 그대로 읽혀도 AI 실행이 매번 막히지 않는다
        Assert.False(store.TryTakeReset());
        Assert.True(File.Exists(Paths.SettingsFile));
    }

    [Fact]
    public async Task The_router_refuses_the_first_run_after_a_real_corrupt_file_then_passes()
    {
        await WriteSettingsAsync("{ 이건 json 이 아니다");
        var store = NewStore();
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), Paths, store);
        var router = new RoutingAiDriver(store, new FakeCredentialStore(), cli, new NoServices());
        var request = new AiRequest("질문", new AiContext("p", null, AiAccessLevel.Plan));
        await store.LoadAsync(default); // 앱 시작 때의 첫 읽기: 손상 파일을 옮기고 기본값으로 시작

        var first = new List<AiEvent>();
        await foreach (var e in router.RunAsync(request, CancellationToken.None))
        {
            first.Add(e);
        }

        var second = new List<AiEvent>();
        await foreach (var e in router.RunAsync(request, CancellationToken.None))
        {
            second.Add(e);
        }

        Assert.Equal("settings_reset", Assert.IsType<AiError>(Assert.Single(first)).Code);
        Assert.Equal("session_required", Assert.IsType<AiError>(Assert.Single(second)).Code); // 기본값 = Claude Code → CliDriver 까지 간다
    }

    [Fact]
    public async Task A_future_version_file_is_read_only_and_leaves_no_reset_signal()
    {
        await WriteSettingsAsync("""{ "schemaVersion": 99, "ui": { "theme": "Dark" } }""");
        var store = NewStore();

        var doc = await store.LoadAsync(default);

        Assert.True(doc.IsReadOnly);
        Assert.False(store.TryTakeReset());
    }

    [Fact]
    public async Task Saved_profiles_round_trip_through_the_real_store()
    {
        var id = Guid.Parse(ProfileId);
        var profile = new AiLlmProfile
        {
            Id = id,
            Name = "사내",
            Preset = AiProfilePreset.Custom,
            Format = AiApiFormat.OpenAi,
            BaseUrl = "http://llm.example:8080",
            Model = "m",
            SupportsImages = true,
        };

        await NewStore().SaveAsync(new AppSettings { Ai = new AiSettings { Profiles = [profile], ActiveProfileId = id } }, default);
        var loaded = (await NewStore().LoadAsync(default)).Value.Ai;

        Assert.Equal(id, loaded.ActiveProfileId);
        Assert.Equal(profile, Assert.Single(loaded.Profiles));
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
