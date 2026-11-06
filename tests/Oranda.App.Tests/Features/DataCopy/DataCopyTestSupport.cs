using Oranda.App.Features.DataCopy;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>2026-11-06 12:00 (UTC) 로 멈춘 시계. 기간 계산 시험이 쓴다.</summary>
internal sealed class FixedTime : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>호출 순서와 인자를 기록하는 가짜 작업 저장소.</summary>
internal sealed class FakeJobStore : ICopyJobStore
{
    public List<string> Calls { get; } = [];

    public Dictionary<string, CopyJob> Jobs { get; } = [];

    public Task<IReadOnlyList<CopyJobEntry>> ListAsync(CancellationToken ct)
    {
        Calls.Add("list");
        return Task.FromResult<IReadOnlyList<CopyJobEntry>>([.. Jobs.Values.Select(j => new CopyJobEntry(j, "/jobs/" + j.Id + ".json", false, null))]);
    }

    /// <summary>설정하면 저장할 때 이 예외를 던진다(저장 실패 재현).</summary>
    public Exception? SaveFailure { get; set; }

    public Task SaveAsync(CopyJob job, CancellationToken ct)
    {
        if (SaveFailure is not null)
        {
            return Task.FromException(SaveFailure);
        }

        Calls.Add("save:" + job.Name);
        Jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<CopyJob> DuplicateAsync(string id, string newName, CancellationToken ct)
    {
        Calls.Add($"duplicate:{id}:{newName}");
        var copy = Jobs[id] with { Id = Guid.NewGuid().ToString("N"), Name = newName };
        Jobs[copy.Id] = copy;
        return Task.FromResult(copy);
    }

    public Task RenameAsync(string id, string newName, CancellationToken ct)
    {
        Calls.Add($"rename:{id}:{newName}");
        Jobs[id] = Jobs[id] with { Name = newName };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        Calls.Add("delete:" + id);
        Jobs.Remove(id);
        return Task.CompletedTask;
    }

    public Task ExportAsync(string id, string path, CancellationToken ct)
    {
        Calls.Add($"export:{id}:{path}");
        return Task.CompletedTask;
    }

    public Task<CopyJob> ImportAsync(string path, CancellationToken ct)
    {
        Calls.Add("import:" + path);
        var job = TestJobs.Job("가져온 작업");
        Jobs[job.Id] = job;
        return Task.FromResult(job);
    }
}

internal sealed class FakePrecheck(PrecheckReport report) : ICopyPrecheckService
{
    public int Calls { get; private set; }

    /// <summary>설정하면 점검이 이 작업이 끝날 때까지 기다린다(점검이 도는 중 상태 재현).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress, CancellationToken ct)
    {
        Calls++;
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return report;
    }
}

internal sealed class FakeRunner : ICopyRunner
{
    public int Calls { get; private set; }

    public CopyDecisions? Decisions { get; private set; }

    public CancellationToken Token { get; private set; }

    public Func<CancellationToken, Task<CopyRunResult>> Behavior { get; set; } = _ => Task.FromResult(TestJobs.Result());

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period,
        IProgress<CopyProgress>? progress, CopyRunControl control, CancellationToken ct)
    {
        Calls++;
        Decisions = decisions;
        Token = ct;
        Started.TrySetResult();
        return Behavior(ct);
    }

    public int RetryCalls { get; private set; }

    public Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report, IReadOnlyList<FailedRow> rows,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        RetryCalls++;
        return Task.FromResult(TestJobs.Result());
    }
}

internal sealed class FakeRunLog : ICopyRunLog
{
    public List<UnfinishedRun> Unfinished { get; } = [];

    public List<string> Recovered { get; } = [];

    public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UnfinishedRun>>([.. Unfinished]);

    public Task MarkRecoveredAsync(string runFile, CancellationToken ct)
    {
        Recovered.Add(runFile);
        return Task.CompletedTask;
    }
}

/// <summary>시험용 모델 값 만들기.</summary>
internal static class TestJobs
{
    public static ConnectionProfile Profile(string name, ConnectionEnvironment env, int idSeed) =>
        TestData.Profile(env) with { Name = name, User = name.ToLowerInvariant() + "_user", Id = new Guid(idSeed, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]) };

    public static CopyJob Job(string name, string source = "SRC", string target = "TGT", params TableRule[] rules) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Source = new ConnectionRef(source, source.ToLowerInvariant() + "_user"),
        Target = new ConnectionRef(target, target.ToLowerInvariant() + "_user"),
        SourceOwner = "APP",
        TargetOwner = "APP",
        MaskSalt = MaskFunctions.NewSalt(),
        Tables = rules,
    };

    public static TableRule Rule(string table, RuleKind kind = RuleKind.All) => new(table, kind, TargetActions.DefaultFor(kind, hasPrimaryKey: false));

    public static TableInfo Table(string name, params ColumnInfo[] columns) => new(new DbObjectRef("APP", name, "TABLE"), columns);

    public static ColumnInfo Column(string name, string type, int position = 1, int? length = null) => new(name, position, type) { Length = length };

    public static CopyRunResult Result(CopyOutcome outcome = CopyOutcome.Succeeded, params FailedRow[] failed) =>
        new(outcome, [new TableRunResult("ORDERS", 10, 10, TimeSpan.FromSeconds(1), outcome, null)], failed, [], new DateTimeOffset(2026, 11, 6, 12, 0, 1, TimeSpan.Zero));

    public static PrecheckReport Report(IReadOnlyList<CheckItem>? items = null, IReadOnlyList<CopyTablePlan>? order = null, IReadOnlyList<PreviewStatement>? preview = null) =>
        new(items ?? [], order ?? [], preview ?? [], new Dictionary<string, long>());

    public static CopyTablePlan Plan(string table, bool canDisableTriggers = false) =>
        new(Rule(table), Table(table), Table(table), [], [])
        {
            CanDisableTriggers = canDisableTriggers,
        };
}

/// <summary>점검·실행 흐름 시험에 필요한 서비스 묶음을 만든다.</summary>
internal sealed class DataCopyHarness
{
    public FakeJobStore Store { get; } = new();

    public FakePrecheck Precheck { get; set; } = new(TestJobs.Report());

    public FakeRunner Runner { get; } = new();

    public FakeRunLog RunLog { get; } = new();

    public List<ConnectionProfile> Opened { get; } = [];

    public List<string> Statements { get; } = [];

    public ConnectionProfile Source { get; } = TestJobs.Profile("SRC", ConnectionEnvironment.Development, 1);

    public ConnectionProfile Target { get; } = TestJobs.Profile("TGT", ConnectionEnvironment.Test, 2);

    public ConnectionProfile Production { get; } = TestJobs.Profile("PRODDB", ConnectionEnvironment.Production, 3);

    public FakeMetadataProvider Metadata { get; } = new();

    /// <summary>접속 목록. 시험이 환경 라벨을 바꾸면 다음에 읽을 때 새 값이 보인다.</summary>
    public List<ConnectionProfile> Profiles { get; }

    public List<FakeSession> OpenedSessions { get; } = [];

    public DataCopyHarness() => Profiles = [Source, Target, Production];

    public DataCopyServices Services() => new()
    {
        Store = Store,
        Precheck = Precheck,
        Runner = Runner,
        RunLog = RunLog,
        Metadata = Metadata,
        Time = new FixedTime(),
        ProfileLoader = _ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([.. Profiles]),
        SessionOpener = (profile, _) =>
        {
            Opened.Add(profile);
            var session = new FakeSession(profile);
            OpenedSessions.Add(session);
            return Task.FromResult<ISession?>(session);
        },
        StatementRunner = (_, sql, _) =>
        {
            Statements.Add(sql);
            return Task.FromResult<string?>(null);
        },
    };

    /// <summary>작업 하나를 열고(접속 다시 잇기까지) 점검 단계 전 상태로 만든다.</summary>
    public async Task<DataCopyViewModel> OpenAsync(CopyJob job)
    {
        Store.Jobs[job.Id] = job;
        var vm = new DataCopyViewModel(Services());
        await vm.InitializeAsync();
        await vm.OpenJobAsync(job);
        return vm;
    }
}
