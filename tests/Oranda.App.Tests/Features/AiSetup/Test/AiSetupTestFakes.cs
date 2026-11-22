using System.Text;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Test;

/// <summary>연결 시험만 하는 가짜 claude 준비 서비스. 결과를 차례로 돌려주고, 멈춤 문이 있으면 열릴 때까지 기다린다.</summary>
internal sealed class FakeClaudeSetup : IClaudeSetupService
{
    public Queue<AiSetupTestResult> Results { get; } = new();

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>있으면 멈추라는 말을 받아도 이것이 열릴 때까지 풀리지 않는다(느리게 끝나는 claude).</summary>
    public TaskCompletionSource? SlowCancel { get; set; }

    /// <summary>있으면 시험이 결과 대신 이 예외를 낸다.</summary>
    public Exception? Throw { get; set; }

    public int Tests { get; private set; }

    public bool Cancelled { get; private set; }

    public ClaudeInstallation? Installation { get; set; } = new("/home/u/.local/bin/claude", "2.1.330");

    public Task<ClaudeInstallation?> FindAsync(CancellationToken ct) => Task.FromResult(Installation);

    public Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct) => throw new NotSupportedException();

    public Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct) => throw new NotSupportedException();

    public async Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct)
    {
        Tests++;
        if (Throw is { } error)
        {
            throw error;
        }

        if (Gate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                if (SlowCancel is { } slow)
                {
                    await slow.Task;
                }

                throw;
            }
        }

        return Results.Count > 0 ? Results.Dequeue() : new AiSetupTestResult(true, TimeSpan.FromSeconds(1));
    }
}

/// <summary>연결 시험만 하는 가짜 프로필 조회. 받은 요청을 기억한다.</summary>
internal sealed class FakeProbe(AiConnectionTestResult result) : IAiProfileProbe
{
    public List<AiProfileProbeRequest> Requests { get; } = [];

    public Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(result);
    }
}

internal static class AiSetupTestRig
{
    public const string SecretKey = "sk-test-SECRET-123456";

    public static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 12, 0, TimeSpan.FromHours(9));

    public static AiLlmProfile Profile { get; } = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "SAMPLE-PROFILE",
        BaseUrl = "https://ai.example.com/v1",
        Model = "big-model",
        Format = AiApiFormat.OpenAi,
    };

    public static AiSetupContext ClaudeContext(FakeClaudeSetup? claude, bool english = false) =>
        new(new SettingsService(new InMemorySettingsStore()))
        {
            Text = new Oranda.App.Shell.ShellText(language: english ? "en" : "ko"),
            Claude = claude,
            AppVersion = "0.1.5",
            ChosenPath = AiSetupPath.ClaudeAccount,
        };

    public static async Task<(AiSetupContext Context, InMemoryCredentialStore Credentials)> ServerContext(FakeProbe probe, bool withProfile = true)
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        if (withProfile)
        {
            await settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = [Profile] } });
        }

        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync(AiProfileCredentials.KeyFor(Profile.Id), Encoding.UTF8.GetBytes(SecretKey));
        var context = new AiSetupContext(settings)
        {
            Text = new Oranda.App.Shell.ShellText(language: "ko"),
            Probe = probe,
            Credentials = credentials,
            AppVersion = "0.1.5",
            ChosenPath = AiSetupPath.CompanyServer,
        };
        context.CreatedProfileId = Profile.Id;
        return (context, credentials);
    }
}
