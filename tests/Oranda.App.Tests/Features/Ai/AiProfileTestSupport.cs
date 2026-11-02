using System.Text;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>저장 실패·읽기 전용을 흉내 낼 수 있는 메모리 설정 저장소. 읽기 전용이면 운영 저장소처럼 말없이 돌아온다(SettingsService 가 먼저 확인해야 한다).</summary>
internal sealed class TestSettingsStore(AppSettings initial, bool readOnly = false) : ISettingsStore
{
    public AppSettings Saved { get; private set; } = initial;

    public int SaveCalls { get; private set; }

    public bool FailSave { get; set; }

    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Saved, readOnly));

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        SaveCalls++;
        if (FailSave)
        {
            throw new IOException("disk full (test)");
        }

        if (!readOnly)
        {
            Saved = settings;
        }

        return Task.CompletedTask;
    }
}

/// <summary>실패를 낼 수 있는 키 저장소.</summary>
internal sealed class TestCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

    public bool FailSave { get; set; }

    public bool FailDelete { get; set; }

    /// <summary>읽기 실패. Windows 의 DPAPI 가 깨진 키 파일에서 던지는 <see cref="System.Security.Cryptography.CryptographicException"/> 을 흉내 낸다.</summary>
    public bool FailLoad { get; set; }

    /// <summary>복원용 SaveAsync 만 실패시키려고 N 번째 이후 쓰기를 막는다(0 = 막지 않음).</summary>
    public int FailSaveAfter { get; set; }

    private int _saves;

    public IReadOnlyCollection<string> Keys => _items.Keys;

    public string? Get(string key) => _items.TryGetValue(key, out var v) ? Encoding.UTF8.GetString(v) : null;

    public void Put(string key, string secret) => _items[key] = Encoding.UTF8.GetBytes(secret);

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        _saves++;
        if (FailSave || (FailSaveAfter > 0 && _saves > FailSaveAfter))
        {
            throw new IOException("credential store unavailable (test)");
        }

        _items[key] = secret.ToArray();
        return Task.CompletedTask;
    }

    /// <summary>첫 읽기만 이 문이 열릴 때까지 붙잡아 「키를 읽는 중」 을 흉내 낸다(두 번째 읽기부터는 바로 돌려준다).</summary>
    public TaskCompletionSource? HoldFirstLoad { get; set; }

    public async Task<byte[]?> LoadAsync(string key)
    {
        if (HoldFirstLoad is { } hold)
        {
            HoldFirstLoad = null;
            await hold.Task.ConfigureAwait(false);
        }

        return FailLoad
            ? throw new System.Security.Cryptography.CryptographicException("cannot unprotect (test)")
            : _items.TryGetValue(key, out var v) ? v.ToArray() : null;
    }

    public Task DeleteAsync(string key)
    {
        if (FailDelete)
        {
            throw new IOException("credential store unavailable (test)");
        }

        _items.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 서버 조회 가짜. 운영 구현과 같은 약속을 지킨다: 실패는 예외가 아니라 결과로 돌려주고, 취소만 <see cref="OperationCanceledException"/> 이다.
/// <see cref="Gate"/> 를 두면 그 작업이 풀릴 때까지(또는 취소될 때까지) 기다린다.
/// </summary>
internal sealed class FakeProbe : IAiProfileProbe
{
    public AiModelListResult ModelResult { get; set; } = AiModelListResult.Ok([]);

    public AiConnectionTestResult TestResult { get; set; } = new(true, TimeSpan.FromSeconds(1));

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>호출 쪽이 취소하지 않았는데도 던지는 취소(구현 자체의 시간 초과).</summary>
    public bool ThrowUnrequestedCancel { get; set; }

    /// <summary>취소된 뒤 취소 예외가 아닌 다른 예외로 끝나는 구현(소켓이 닫히며 IOException 을 내는 경우)을 흉내 낸다.</summary>
    public bool ThrowOtherAfterCancel { get; set; }

    /// <summary>취소를 무시하고 문이 열리면 정상 결과를 돌려주는 구현(취소가 늦게 닿은 경우).</summary>
    public bool IgnoreCancel { get; set; }

    public AiProfileProbeRequest? LastRequest { get; private set; }

    public int Calls { get; private set; }

    public async Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct)
    {
        LastRequest = request;
        Calls++;
        await WaitAsync(ct).ConfigureAwait(false);
        return ModelResult;
    }

    public async Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct)
    {
        LastRequest = request;
        Calls++;
        await WaitAsync(ct).ConfigureAwait(false);
        return TestResult;
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (IgnoreCancel)
        {
            if (Gate is { } open)
            {
                await open.Task.ConfigureAwait(false);
            }

            return;
        }

        ct.ThrowIfCancellationRequested();
        if (ThrowUnrequestedCancel)
        {
            throw new OperationCanceledException();
        }

        if (Gate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ThrowOtherAfterCancel)
            {
                throw new IOException("connection closed after cancel (test)");
            }
        }
    }
}

internal sealed class ProfileHarness
{
    public required SettingsService Settings { get; init; }

    public required TestSettingsStore Store { get; init; }

    public required TestCredentialStore Credentials { get; init; }

    public required FakeProbe Probe { get; init; }

    public required FakeEventBus Bus { get; init; }

    public AiProfileServices Services { get; init; } = null!;

    public AiSettings Ai => Settings.Current.Ai;

    public static async Task<ProfileHarness> CreateAsync(AiSettings? ai = null, bool readOnly = false, bool withProbe = true, bool withCredentials = true)
    {
        var store = new TestSettingsStore(new AppSettings { Ai = ai ?? new AiSettings() }, readOnly);
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        var credentials = new TestCredentialStore();
        var probe = new FakeProbe();
        var bus = new FakeEventBus();
        return new ProfileHarness
        {
            Settings = settings,
            Store = store,
            Credentials = credentials,
            Probe = probe,
            Bus = bus,
            Services = new AiProfileServices(settings, withCredentials ? credentials : null, withProbe ? probe : null, bus),
        };
    }

    public AiModelSettingsPage Page() => new(Ai, Services);

    public AiProfileManagerViewModel Manager(Func<string, Task<bool>>? confirm = null, Action<AiSettings, Guid?, Guid?>? applied = null, Guid? select = null, bool startNew = false) =>
        new(Services, confirm ?? (_ => Task.FromResult(true)), applied, select, startNew);
}

internal static class ProfileSamples
{
    public static AiLlmProfile Qwen(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "사내 Qwen",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.Anthropic,
        BaseUrl = "http://llm.example:8080",
        Model = "qwen-example",
        SupportsImages = true,
    };

    public static AiLlmProfile Ollama(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "로컬 Ollama",
        Preset = AiProfilePreset.Ollama,
        Format = AiApiFormat.OpenAi,
        BaseUrl = "http://localhost:11434/v1",
        Model = "gemma-example",
    };

    public static AiSettings With(params AiLlmProfile[] profiles) => new() { Profiles = profiles };
}
