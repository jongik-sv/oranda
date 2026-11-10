using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Composition;

// 소유: xdb-copy XC6. 복사 디스패처: 같은 DBMS = 키 등록 구현, Oracle↔Tibero = 두 DBMS 의 부품 벌로 Core 실행기·점검을 만든다, 부품 없음·지원 안 하는 쌍 = NotSupported.
public class CopyCrossDispatchTests
{
    private static FakeSession Session(Dbms dbms, bool readOnly = false, ConnectionEnvironment environment = ConnectionEnvironment.Development) =>
        new(TestData.Profile(environment, readOnly) with { Dbms = dbms }, TestData.Server());

    private static CopyJob Job() => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = [],
    };

    private static CopyRunnerDispatcher Runner(DbmsServiceTable table) =>
        new(table, new NoBulk(), new FakeExecutionService(), new NoRunLog());

    /// <summary>점검 디스패처(교차 경로가 쓰는 대량 실행·메타데이터·정책은 이 시험들이 쓰지 않는 가짜). <see cref="DbmsDispatcherTests"/> 도 쓴다.</summary>
    internal static CopyPrecheckServiceDispatcher Precheck(DbmsServiceTable table) =>
        new(table, new NoBulk(), new FakeMetadataProvider(), new AllowPolicy());

    [Fact]
    public async Task Same_dbms_copy_goes_to_the_keyed_runner_without_touching_the_parts()
    {
        var keyed = new CountingRunner();
        var oracleParts = new FakeParts(Dbms.Oracle);
        var table = new DbmsServiceTable()
            .Add<ICopyRunner>(Dbms.Oracle, () => keyed)
            .Add<ICopyDbmsParts>(Dbms.Oracle, () => oracleParts);

        await Runner(table).RetryFailedRowsAsync(new CopySessions(Session(Dbms.Oracle), Session(Dbms.Oracle)), null!, null!, [], null, default);

        Assert.Equal(1, keyed.Runs);
        Assert.Equal(0, oracleParts.Reads);
    }

    [Theory]
    [InlineData(Dbms.Oracle, Dbms.Tibero)]
    [InlineData(Dbms.Tibero, Dbms.Oracle)]
    public async Task Cross_copy_builds_a_core_runner_from_both_dbms_parts(Dbms source, Dbms target)
    {
        var keyed = new CountingRunner();
        var sourceParts = new FakeParts(source);
        var targetParts = new FakeParts(target);
        var table = new DbmsServiceTable()
            .Add<ICopyRunner>(Dbms.Oracle, () => keyed)
            .Add<ICopyRunner>(Dbms.Tibero, () => keyed)
            .Add<ICopyDbmsParts>(source, () => sourceParts)
            .Add<ICopyDbmsParts>(target, () => targetParts);
        var report = new PrecheckReport([new CheckItem(null, "X", CheckLevel.Error, "오류")], [], [], new Dictionary<string, long>());

        // Core 실행기가 받았다는 표시: 점검 오류가 있는 보고서를 Core 실행기가 거부한다(키 등록 실행기는 불리지 않는다).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(table).RunAsync(
            new CopySessions(Session(source, readOnly: true), Session(target)), Job(), report,
            new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
            new ResolvedPeriod(null, null), null, new CopyRunControl(), default));

        Assert.Contains("사전 점검에 오류", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, keyed.Runs);
        Assert.Equal(1, sourceParts.Reads);
        Assert.Equal(1, targetParts.Reads);
    }

    [Fact]
    public async Task Cross_precheck_builds_a_core_precheck_from_both_dbms_parts()
    {
        var table = new DbmsServiceTable()
            .Add<ICopyDbmsParts>(Dbms.Oracle, () => new FakeParts(Dbms.Oracle))
            .Add<ICopyDbmsParts>(Dbms.Tibero, () => new FakeParts(Dbms.Tibero));

        // 운영 대상이면 Core 점검이 조회 없이 이 오류 하나만 낸다(키 등록 점검이 없어도 답한다).
        var report = await Precheck(table).RunAsync(
            new CopySessions(Session(Dbms.Tibero, readOnly: true), Session(Dbms.Oracle, environment: ConnectionEnvironment.Production)),
            Job(), new ResolvedPeriod(null, null), null, default);

        Assert.Equal("TargetProduction", Assert.Single(report.Items).Code);
    }

    [Fact]
    public async Task Cross_copy_without_parts_on_one_side_is_not_supported()
    {
        var table = new DbmsServiceTable().Add<ICopyDbmsParts>(Dbms.Oracle, () => new FakeParts(Dbms.Oracle));
        var sessions = new CopySessions(Session(Dbms.Oracle, readOnly: true), Session(Dbms.Tibero));

        var run = await Assert.ThrowsAsync<NotSupportedException>(() => Runner(table).RetryFailedRowsAsync(sessions, Job(), null!, [], null, default));
        var check = await Assert.ThrowsAsync<NotSupportedException>(() => Precheck(table).RunAsync(sessions, Job(), new ResolvedPeriod(null, null), null, default));

        Assert.IsNotType<DbmsNotSupportedException>(run);
        Assert.Contains("Tibero", run.Message, StringComparison.Ordinal);
        Assert.IsNotType<DbmsNotSupportedException>(check);
    }

    [Fact]
    public void Only_oracle_and_tibero_in_either_direction_is_a_cross_copy_pair()
    {
        Assert.True(DispatchRules.IsCrossCopyPair(Dbms.Oracle, Dbms.Tibero));
        Assert.True(DispatchRules.IsCrossCopyPair(Dbms.Tibero, Dbms.Oracle));
        Assert.False(DispatchRules.IsCrossCopyPair(Dbms.Oracle, Dbms.Oracle));
        Assert.False(DispatchRules.IsCrossCopyPair(Dbms.Tibero, Dbms.Tibero));
    }

    // ---- 가짜 협력자 ----

    /// <summary>부품 한 벌(분류기만 진짜 가짜, 나머지는 이 시험이 쓰지 않는다). <see cref="Reads"/> = 벌을 꺼낸 횟수.</summary>
    private sealed class FakeParts(Dbms dbms) : ICopyDbmsParts
    {
        private readonly CopyDbmsParts _parts = new(dbms, null!, new FakeClassifier(dbms), null!, null!, null!, null!);

        public int Reads { get; private set; }

        public CopyDbmsParts Parts
        {
            get
            {
                Reads++;
                return _parts;
            }
        }
    }

    private sealed class FakeClassifier(Dbms dbms) : IDbErrorClassifier
    {
        public Dbms Dbms => dbms;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    private sealed class CountingRunner : ICopyRunner
    {
        public int Runs { get; private set; }

        public Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period,
                                            IProgress<CopyProgress>? progress, CopyRunControl control, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult<CopyRunResult>(null!);
        }

        public Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report, IReadOnlyList<FailedRow> rows,
                                                        IProgress<CopyProgress>? progress, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult<CopyRunResult>(null!);
        }
    }

    private sealed class NoBulk : IBulkExecutionService
    {
        public IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                           ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException("이 시험은 대량 실행을 쓰지 않는다.");

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException("이 시험은 대량 실행을 쓰지 않는다.");
    }

    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class AllowPolicy : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }
}
