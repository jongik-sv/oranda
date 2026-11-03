using System.Runtime.CompilerServices;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

internal sealed class FixedEndpoint(string? pipeName) : IAgentBridgeEndpoint
{
    public string? PipeName { get; } = pipeName;
}

internal sealed class FixedSettings(AppSettings settings) : ISettingsStore
{
    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(settings));

    public Task SaveAsync(AppSettings value, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeLauncher : IClaudeProcessLauncher
{
    public string[] Lines { get; set; } = [];

    public string StandardError { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public bool Blocking { get; set; }

    /// <summary>true 면 <see cref="FakeProcess.Kill"/> 이 예외를 낸다(정리가 끝까지 도는지 확인).</summary>
    public bool KillThrows { get; set; }

    public Action<ClaudeLaunch>? OnStart { get; set; }

    public List<ClaudeLaunch> Launches { get; } = [];

    public FakeProcess? Process { get; private set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IClaudeProcess Start(ClaudeLaunch launch)
    {
        Launches.Add(launch);
        OnStart?.Invoke(launch);
        Process = new FakeProcess(this);
        Started.TrySetResult();
        return Process;
    }
}

internal sealed class FakeProcess(FakeLauncher launcher) : IClaudeProcess
{
    public bool Killed { get; private set; }

    public string StandardErrorTail => launcher.StandardError;

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (launcher.Blocking)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        foreach (var line in launcher.Lines)
        {
            yield return line;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(launcher.ExitCode);

    public void Kill()
    {
        Killed = true;
        if (launcher.KillThrows)
        {
            throw new InvalidOperationException("kill failed");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
