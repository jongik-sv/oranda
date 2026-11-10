using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 복사 대기 원인 감시(<see cref="CopyWaitProbe"/>, WH1): 30초 문턱, 15초 안 다시 쓰기, 감시 세션 하나 나눠 쓰기·마지막 대기가 끝나면 닫기,
/// 볼 권한 없음 → 조용히 끄고 안내 한 번, 대기 줄 글(<see cref="CopyWaitCause.Describe"/>). 서버 없음, 가짜 협력자·손으로 돌리는 시계.
/// </summary>
public sealed class CopyWaitProbeTests
{
    private static readonly TimeSpan Over = TimeSpan.FromSeconds(31);

    private static readonly object?[] RowLock =
        ["enq: TX - row lock contention", "Application", 42L, 123L, "APPUSER", "JDBC Thin Client", "was01"];

    // ---- 30초 문턱 ----

    [Fact]
    public async Task Under_30_seconds_returns_null_and_never_opens_the_monitor()
    {
        var f = new Fixture(RowLock);

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), TimeSpan.FromSeconds(29), default));

        Assert.Equal(0, f.Opens);
        Assert.Empty(f.Bulk.Calls);
        Assert.False(f.Probe.HasMonitorSession);
    }

    [Fact]
    public async Task Over_30_seconds_opens_the_monitor_once_binds_sid_and_instance_as_text_and_describes_the_row_lock_with_its_blocker()
    {
        var f = new Fixture(RowLock);
        var copy = Copy(77);

        var text = await f.Probe.DescribeAsync(copy, Over, default);

        Assert.Equal("행 잠금(enq: TX) · 막는 세션 123 (APPUSER@was01, JDBC Thin Client)", text);
        Assert.Equal(1, f.Opens);
        Assert.True(f.Probe.HasMonitorSession);
        var call = Assert.Single(f.Bulk.Calls);
        Assert.Same(f.Monitors[0], call.Session); // 복사 세션이 아니라 감시 세션으로 읽는다
        Assert.Equal("copy.wait_cause", call.Sql);
        Assert.Equal("INST,SID", string.Join(',', call.Binds.Keys.Order(StringComparer.Ordinal)));
        Assert.Equal(new BindValue(BindDataType.Varchar2, "77"), call.Binds["SID"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "FREE2"), call.Binds["INST"]);
        Assert.False(f.Probe.IsOff);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Copy_session_without_a_sid_returns_null_without_opening_the_monitor()
    {
        var f = new Fixture(RowLock);

        Assert.Null(await f.Probe.DescribeAsync(Copy(0), Over, default));

        Assert.Equal(0, f.Opens);
        Assert.Empty(f.Bulk.Calls);
    }

    // ---- 15초 간격 ----

    [Fact]
    public async Task Second_call_within_15_seconds_reuses_the_text_and_after_15_seconds_queries_again()
    {
        var f = new Fixture(RowLock);
        var copy = Copy(77);
        var first = await f.Probe.DescribeAsync(copy, Over, default);

        f.Bulk.Rows = [["enq: TM - contention", "Application", 50L, 124L, "BATCH", null, null]];
        f.Time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(first, await f.Probe.DescribeAsync(copy, Over + TimeSpan.FromSeconds(14), default));
        Assert.Single(f.Bulk.Calls);

        f.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("테이블 잠금(enq: TM) · 막는 세션 124 (BATCH)", await f.Probe.DescribeAsync(copy, Over + TimeSpan.FromSeconds(16), default));
        Assert.Equal(2, f.Bulk.Calls.Count);
        Assert.Equal(1, f.Opens); // 감시 세션은 그대로 다시 쓴다
    }

    [Fact]
    public async Task Session_not_found_is_null_and_is_also_reused_within_15_seconds()
    {
        var f = new Fixture();
        var copy = Copy(77);

        Assert.Null(await f.Probe.DescribeAsync(copy, Over, default));
        f.Bulk.Rows = [RowLock];
        f.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await f.Probe.DescribeAsync(copy, Over, default));

        Assert.Single(f.Bulk.Calls);
        Assert.False(f.Probe.IsOff); // 세션을 못 찾은 것은 끌 이유가 아니다
    }

    // ---- 감시 세션 하나 ----

    [Fact]
    public async Task Two_copy_sessions_share_one_monitor_session()
    {
        var f = new Fixture(RowLock);

        await f.Probe.DescribeAsync(Copy(77), Over, default);
        await f.Probe.DescribeAsync(Copy(78), Over, default);

        Assert.Equal(1, f.Opens);
        Assert.Equal(2, f.Bulk.Calls.Count);
        Assert.All(f.Bulk.Calls, c => Assert.Same(f.Monitors[0], c.Session));
        Assert.Equal("77,78", string.Join(',', f.Bulk.Calls.Select(c => c.Binds["SID"].Value as string)));
    }

    [Fact]
    public async Task End_wait_closes_the_monitor_only_after_the_last_waiter_ends_and_the_next_wait_opens_a_new_one()
    {
        var f = new Fixture(RowLock);
        var a = Copy(77);
        var b = Copy(78);
        await f.Probe.DescribeAsync(a, Over, default);
        await f.Probe.DescribeAsync(b, Over, default);

        await f.Probe.EndWaitAsync(a);
        Assert.True(f.Probe.HasMonitorSession);
        Assert.Equal(0, f.Monitors[0].DisposeCount);

        await f.Probe.EndWaitAsync(b);
        Assert.False(f.Probe.HasMonitorSession);
        Assert.Equal(1, f.Monitors[0].DisposeCount);

        // 다음 대기: 앞 값을 버렸으므로 15초 안이라도 새 감시 세션으로 다시 읽는다.
        Assert.NotNull(await f.Probe.DescribeAsync(a, Over, default));
        Assert.Equal(2, f.Opens);
        Assert.Equal(3, f.Bulk.Calls.Count);
        Assert.Same(f.Monitors[1], f.Bulk.Calls[^1].Session);
    }

    [Fact]
    public async Task Dispose_closes_the_monitor_and_later_calls_return_null()
    {
        var f = new Fixture(RowLock);
        await f.Probe.DescribeAsync(Copy(77), Over, default);

        await f.Probe.DisposeAsync();
        await f.Probe.DisposeAsync();

        Assert.Equal(1, f.Monitors[0].DisposeCount);
        Assert.False(f.Probe.HasMonitorSession);
        Assert.Null(await f.Probe.DescribeAsync(Copy(78), Over, default));
        Assert.Equal(1, f.Opens);
    }

    // ---- 꺼짐 ----

    [Fact]
    public async Task No_access_server_error_turns_the_probe_off_and_the_notice_is_taken_exactly_once()
    {
        var f = new Fixture(RowLock);
        f.Bulk.Failure = new FakeDbException(942);

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.True(f.Probe.IsOff);
        Assert.False(f.Probe.HasMonitorSession);
        Assert.Equal(1, f.Monitors[0].DisposeCount); // 열었던 감시 세션은 닫는다
        Assert.Equal(CopyWaitProbe.NoAccessNotice, f.Probe.TakeNotice());
        Assert.Null(f.Probe.TakeNotice());

        // 이번 실행에서는 더 묻지 않는다(간격이 지나도, 다른 복사 세션도).
        f.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));
        Assert.Null(await f.Probe.DescribeAsync(Copy(78), Over, default));
        Assert.Single(f.Bulk.Calls);
        Assert.Equal(1, f.Opens);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Insufficient_privileges_is_also_no_access()
    {
        var f = new Fixture(RowLock);
        f.Bulk.Failure = new FakeDbException(1031);

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.True(f.Probe.IsOff);
        Assert.Equal(CopyWaitProbe.NoAccessNotice, f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Other_server_error_turns_the_probe_off_without_a_notice()
    {
        var f = new Fixture(RowLock);
        f.Bulk.Failure = new FakeDbException(600);

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.True(f.Probe.IsOff);
        Assert.False(f.Probe.HasMonitorSession);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Wrapped_no_access_error_is_not_a_server_error_and_gives_no_notice()
    {
        // CopyErrors 는 바깥 예외 자체가 드라이버 예외일 때만 서버 오류로 본다. 감싼 예외도 감시는 끄지만 「권한 없음」 안내는 내지 않는다.
        var f = new Fixture(RowLock);
        f.Bulk.Failure = new InvalidOperationException("감쌈", new FakeDbException(942));

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.True(f.Probe.IsOff);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Open_returning_null_turns_the_probe_off_without_a_notice_and_does_not_retry()
    {
        var f = new Fixture(RowLock) { OpenReturnsNull = true };

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));
        f.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.Equal(1, f.Opens);
        Assert.Empty(f.Bulk.Calls);
        Assert.True(f.Probe.IsOff);
        Assert.False(f.Probe.HasMonitorSession);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Open_failure_turns_the_probe_off_without_a_notice()
    {
        var f = new Fixture(RowLock) { OpenFailure = new InvalidOperationException("접속 실패") };

        Assert.Null(await f.Probe.DescribeAsync(Copy(77), Over, default));

        Assert.True(f.Probe.IsOff);
        Assert.Null(f.Probe.TakeNotice());
    }

    [Fact]
    public async Task Caller_cancellation_is_thrown_and_does_not_turn_the_probe_off()
    {
        var f = new Fixture(RowLock);
        using var cts = new CancellationTokenSource();
        f.Bulk.OnQuery = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Probe.DescribeAsync(Copy(77), Over, cts.Token));

        Assert.False(f.Probe.IsOff);
        Assert.Null(f.Probe.TakeNotice());
    }

    // ---- 행 → 글 ----

    [Fact]
    public async Task Unknown_event_without_blocker_shows_the_raw_event_name()
    {
        var f = new Fixture(["PX Deq: Execute Reply", "Other", 40L, null, null, null, null]);

        Assert.Equal("PX Deq: Execute Reply", await f.Probe.DescribeAsync(Copy(77), Over, default));
    }

    [Fact]
    public async Task No_event_is_db_processing()
    {
        var f = new Fixture([null, null, 0L, null, null, null, null]);

        Assert.Equal("DB 처리 중(대기 이벤트 없음)", await f.Probe.DescribeAsync(Copy(77), Over, default));
    }

    [Fact]
    public async Task Blank_texts_are_null_and_texts_are_trimmed()
    {
        var f = new Fixture(["  ", " ", 0L, 9L, " APPUSER ", "   ", ""]);

        Assert.Equal("DB 처리 중(대기 이벤트 없음) · 막는 세션 9 (APPUSER)", await f.Probe.DescribeAsync(Copy(77), Over, default));
    }

    [Fact]
    public async Task Probe_reads_the_whole_row_without_the_threshold()
    {
        var f = new Fixture(RowLock);

        var cause = await f.Probe.ProbeAsync(Copy(77), default);

        Assert.Equal(new CopyWaitCause("enq: TX - row lock contention", "Application", 42, 123, "APPUSER", "JDBC Thin Client", "was01"), cause);
        Assert.True(f.Probe.HasMonitorSession);
    }

    [Theory]
    [InlineData(null, null, null, null, null, "DB 처리 중(대기 이벤트 없음)")]
    [InlineData("enq: TX - row lock contention", 5L, null, null, null, "행 잠금(enq: TX) · 막는 세션 5")]
    [InlineData("enq: TX - row lock contention", 5L, "APP", null, null, "행 잠금(enq: TX) · 막는 세션 5 (APP)")]
    [InlineData("enq: TX - row lock contention", 5L, null, "sqlplus", "pc1", "행 잠금(enq: TX) · 막는 세션 5 (sqlplus)")]
    [InlineData("enq: TX - index contention", null, null, null, null, "인덱스 분할 경합(enq: TX)")]
    [InlineData("log file sync", null, null, null, null, "리두 로그 기록 대기(log file sync)")]
    [InlineData("db file sequential read", null, null, null, null, "디스크 I/O(db file sequential read)")]
    [InlineData("some tibero wait", 7L, null, null, null, "잠금 대기(some tibero wait) · 막는 세션 7")]
    [InlineData("some tibero wait", null, null, null, null, "some tibero wait")]
    public void Describe_cases(string? evt, long? blocker, string? user, string? program, string? machine, string expected)
    {
        var waitClass = evt == "db file sequential read" ? "User I/O" : null;
        Assert.Equal(expected, new CopyWaitCause(evt, waitClass, 10, blocker, user, program, machine).Describe());
    }

    [Theory]
    [InlineData("enq: TX - row lock contention", "enq: TX")]
    [InlineData("enq: TM - contention", "enq: TM")]
    [InlineData("ENQ: UL - contention", "ENQ: UL")]
    [InlineData("enq: TX", "enq: TX")]
    [InlineData("log file sync", "log file sync")]
    [InlineData("PX Deq - Execute Reply", "PX Deq - Execute Reply")]
    public void ShortEvent_cuts_only_enqueue_names_at_the_dash(string evt, string expected)
    {
        Assert.Equal(expected, CopyWaitCause.ShortEvent(evt));
    }

    [Theory]
    [InlineData("enq: TX - row lock contention", null, false, "행 잠금")]
    [InlineData("enq: TX - index contention", null, false, "인덱스 분할 경합")]
    [InlineData("enq: TX - allocate ITL entry", null, false, "블록 ITL 부족")]
    [InlineData("enq: TM - contention", null, false, "테이블 잠금")]
    [InlineData("enq: UL - contention", null, false, "잠금")]
    [InlineData("library cache lock", null, false, "객체 잠금(DDL·컴파일)")]
    [InlineData("cursor: pin S wait on X", null, false, "객체 잠금(DDL·컴파일)")]
    [InlineData("log file switch completion", null, false, "리두 로그 기록 대기")]
    [InlineData("buffer busy waits", null, false, "블록 경합")]
    [InlineData("SQL*Net message from client", null, false, "앱 쪽 응답 대기")]
    [InlineData("SQL*Net more data from client", null, false, "앱 쪽 응답 대기")]
    [InlineData("SQL*Net message to dblink", null, false, "네트워크 대기")]
    [InlineData("some wait", null, true, "잠금 대기")]
    [InlineData("enq: TX - row lock contention", "Configuration", true, "행 잠금")]
    [InlineData("some wait", "User I/O", false, "디스크 I/O")]
    [InlineData("some wait", "System I/O", false, "디스크 I/O")]
    [InlineData("some wait", "Concurrency", false, "동시성 경합")]
    [InlineData("some wait", "Commit", false, "커밋 대기")]
    [InlineData("some wait", "Configuration", false, "설정 한도 대기")]
    [InlineData("some wait", "Other", false, null)]
    [InlineData("some wait", null, false, null)]
    public void Kind_cases(string evt, string? waitClass, bool blocked, string? expected)
    {
        Assert.Equal(expected, CopyWaitCause.Kind(evt, waitClass, blocked));
    }

    /// <summary>복사 세션(SID = <paramref name="sid"/>, 인스턴스 FREE2).</summary>
    private static FakeSession Copy(long sid) => new(TestData.Profile(), TestData.Server() with { SessionId = sid, InstanceName = "FREE2" });

    // ---- 가짜 협력자 ----

    /// <summary>감시 세션을 여는 함수·가짜 대량 실행·손으로 돌리는 시계를 묶은 한 벌.</summary>
    private sealed class Fixture
    {
        /// <param name="row">감시 조회가 돌려줄 한 행. null = 0행(세션을 찾지 못함).</param>
        public Fixture(object?[]? row = null)
        {
            Bulk = new FakeWaitBulk { Rows = row is null ? [] : [row] };
            Probe = new CopyWaitProbe(OpenAsync, Bulk, new FakeCatalog(), new FakeCodec(), new FakeClassifier(), Time);
        }

        public ManualTimeProvider Time { get; } = new();

        public FakeWaitBulk Bulk { get; }

        public CopyWaitProbe Probe { get; }

        public List<FakeSession> Monitors { get; } = [];

        /// <summary>감시 세션을 연 횟수(null 을 돌려준 것·실패도 센다).</summary>
        public int Opens { get; private set; }

        public bool OpenReturnsNull { get; init; }

        public Exception? OpenFailure { get; init; }

        private Task<ISession?> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Opens++;
            if (OpenFailure is not null)
            {
                return Task.FromException<ISession?>(OpenFailure);
            }

            if (OpenReturnsNull)
            {
                return Task.FromResult<ISession?>(null);
            }

            var monitor = new FakeSession(TestData.Profile(), TestData.Server(), SessionPurpose.Monitor);
            Monitors.Add(monitor);
            return Task.FromResult<ISession?>(monitor);
        }
    }

    /// <summary>카탈로그 글 = id 그대로(가짜 대량 실행이 id 로 알아본다).</summary>
    private sealed class FakeCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    /// <summary>받은 조회를 적어 두고 <see cref="Rows"/> 를 돌려준다. <see cref="Failure"/> 가 있으면 첫 행 전에 던진다.</summary>
    private sealed class FakeWaitBulk : IBulkExecutionService
    {
        public List<(ISession Session, string Sql, IReadOnlyDictionary<string, BindValue> Binds)> Calls { get; } = [];

        public object?[][] Rows { get; set; } = [];

        public Exception? Failure { get; set; }

        /// <summary>조회를 받을 때 부른다(취소 시험).</summary>
        public Action? OnQuery { get; set; }

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Calls.Add((s, query.Text, binds));
            OnQuery?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            foreach (var row in Rows)
            {
                yield return row;
            }
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>드라이버의 서버 오류 흉내(번호만).</summary>
    private sealed class FakeDbException(int number) : DbException("ORA-" + number.ToString("D5", CultureInfo.InvariantCulture))
    {
        public int Number { get; } = number;
    }

    /// <summary>Oracle 분류 흉내: 942 = 객체 없음, 1031 = 권한 부족. 안쪽 예외까지 찾는다(<see cref="IDbErrorClassifier.FromException"/> 계약).</summary>
    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => number switch
        {
            942 => DbErrorKind.ObjectNotFound,
            1031 => DbErrorKind.InsufficientPrivileges,
            _ => DbErrorKind.Other,
        };

        public DbError? FromException(Exception exception)
        {
            for (var e = exception; e is not null; e = e.InnerException)
            {
                if (e is FakeDbException db)
                {
                    return new DbError(db.Message, db.Message) { Number = db.Number, Kind = Classify(db.Number), Dbms = Dbms.Oracle };
                }
            }

            return null;
        }
    }

    private sealed class FakeCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => BulkKind.Other;

        public BindDataType BindTypeOf(string serverType) => BindDataType.Varchar2;

        public string? ToKeyText(object? value, string serverType) => value?.ToString();

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
