using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Oranda.Platform.Ai.Setup;

namespace Oranda.Platform.Tests.Ai.Setup;

/// <summary>가짜 프로세스 하나가 할 일: 낼 줄·종료 코드, 또는 끝내기(Kill) 전까지 멈춰 있기.</summary>
internal sealed record FakeScript(IReadOnlyList<SetupLine> Lines, int ExitCode = 0, bool HangUntilKilled = false, Exception? StartError = null)
{
    public static FakeScript Out(int exitCode, params string[] lines) => new(lines.Select(l => new SetupLine(l, false)).ToList(), exitCode);

    public static FakeScript Hang(params string[] lines) => new(lines.Select(l => new SetupLine(l, false)).ToList(), HangUntilKilled: true);

    public static FakeScript Missing() => new([], StartError: new System.ComponentModel.Win32Exception(2, "No such file or directory"));
}

/// <summary>실제 프로세스를 띄우지 않는 실행기. 띄운 것과 그 뒤 정리를 기록한다.</summary>
internal sealed class FakeSetupRunner(Func<SetupLaunch, FakeScript> script) : ISetupProcessRunner
{
    public List<SetupLaunch> Launches { get; } = [];

    public List<FakeSetupProcess> Processes { get; } = [];

    public ISetupProcess Start(SetupLaunch launch)
    {
        Launches.Add(launch);
        var plan = script(launch);
        if (plan.StartError is not null)
        {
            throw plan.StartError;
        }

        var process = new FakeSetupProcess(plan);
        lock (Processes)
        {
            Processes.Add(process);
        }

        return process;
    }

    /// <summary>모든 가짜 프로세스가 끝났는지(멈춘 것은 Kill 됐는지).</summary>
    public bool NoneLeftRunning
    {
        get
        {
            lock (Processes)
            {
                return Processes.All(p => p.Finished);
            }
        }
    }
}

internal sealed class FakeSetupProcess : ISetupProcess
{
    private readonly FakeScript _script;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<SetupLine> _lines = Channel.CreateUnbounded<SetupLine>();

    public FakeSetupProcess(FakeScript script)
    {
        _script = script;
        foreach (var line in script.Lines)
        {
            _lines.Writer.TryWrite(line);
        }

        if (!script.HangUntilKilled)
        {
            _lines.Writer.TryComplete();
            _exit.TrySetResult(script.ExitCode);
        }
    }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public bool Finished => _exit.Task.IsCompleted;

    public async IAsyncEnumerable<SetupLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    /// <summary>멈춰 있는 프로세스가 나중에 한 줄을 낸다.</summary>
    public void Emit(string line) => _lines.Writer.TryWrite(new SetupLine(line, false));

    public void Kill()
    {
        Killed = true;
        _lines.Writer.TryComplete();
        _exit.TrySetResult(137);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Kill();
        return ValueTask.CompletedTask;
    }
}
