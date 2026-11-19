using System.Collections;
using System.Data;
using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Sessions;

/// <summary>
/// 행 진행 감시(<see cref="TiberoSessionQuery"/>·<see cref="RowProgressWatch"/>): 결과를 받는 도중 드라이버 Read 가 돌아오지 않는 경우(실측: 망이 약 1000행·64KB 를
/// 받다 끊겨 recv 에서 멈춤)를 가짜 리더로 흉내 낸다. 한도는 시험에서 짧게 준다.
/// </summary>
public class TiberoSessionQueryStallTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    private static readonly ConnectionProfile Profile = new("t", "u", "h:1/d", Dbms: Dbms.Tibero);

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static (TiberoSession Session, List<OrandaEvent> Events) Create(ScriptedConnection connection, Func<ValueTask> close)
    {
        var bus = new EventBus();
        var events = new List<OrandaEvent>();
        bus.Subscribe<OrandaEvent>(events, e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        var session = new TiberoSession(Guid.NewGuid(), Profile, Server, SessionPurpose.Work, "D/-/U", connection, null, bus, null, close);
        return (session, events);
    }

    [Fact]
    public async Task A_read_that_never_returns_times_out_breaks_the_session_and_cleans_up_in_the_background()
    {
        using var wake = new ManualResetEventSlim(false);
        // 3행을 준 뒤 4번째 Read 가 돌아오지 않는다(연결을 닫아야 깬다).
        var connection = new ScriptedConnection(row => row < 3 || WaitThenFail(wake));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (session, events) = Create(connection, () =>
        {
            closed.TrySetResult();
            wake.Set(); // 연결을 닫으면 멈춘 수신이 깬다
            return ValueTask.CompletedTask;
        });

        var stall = await Assert.ThrowsAsync<ServerStallException>(
            () => TiberoSessionQuery.RunAsync(session, "SELECT N FROM T", null, int.MaxValue, Stall, default).WaitAsync(Quick));

        Assert.Equal(Stall, stall.Limit);
        Assert.Equal(SessionState.Broken, session.State);
        Assert.True(session.IsStalled);
        lock (events)
        {
            var broken = Assert.Single(events.OfType<SessionBroken>());
            Assert.Equal(DbErrorKind.Disconnected, broken.Error?.Kind);
        }

        // 연결 닫기는 백그라운드에서 시작했고, 멈춘 일이 끝나면 명령 잠금을 놓는다.
        await closed.Task.WaitAsync(Quick);
        await WaitUntil(() => !session.CommandGate.IsHeld);

        // 끊긴 것으로 본 세션의 새 질의는 잠금을 기다리지 않고 바로 실패한다(다시 접속해야 한다).
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => TiberoSessionQuery.RunAsync(session, "SELECT 1 FROM DUAL").WaitAsync(Quick));
        Assert.Equal(TiberoSessionQuery.StalledSessionMessage, again.Message);

        // 세션을 닫아도 연결은 한 번만 닫는다(멈춤 처리가 시작한 닫기를 기다린다).
        await session.DisposeAsync();
        Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    public async Task The_gate_stays_held_until_the_stalled_read_ends()
    {
        using var wake = new ManualResetEventSlim(false);
        var connection = new ScriptedConnection(row => row < 1 || WaitThenFail(wake));
        var (session, _) = Create(connection, () => ValueTask.CompletedTask); // 닫아도 수신이 깨지 않는 드라이버

        await Assert.ThrowsAsync<ServerStallException>(
            () => TiberoSessionQuery.RunAsync(session, "SELECT N FROM T", null, int.MaxValue, Stall, default).WaitAsync(Quick));

        Assert.True(session.CommandGate.IsHeld); // 멈춘 일이 연결을 쥐고 있다: 다른 명령이 끼지 않게(D-041)
        wake.Set();
        await WaitUntil(() => !session.CommandGate.IsHeld);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_slow_read_that_keeps_receiving_rows_is_not_cut()
    {
        // 행마다 60ms(한도 300ms 의 1/5), 15행 = 900ms: 전체로는 한도의 세 배지만 행이 계속 온다.
        var connection = new ScriptedConnection(row =>
        {
            if (row >= 15)
            {
                return false;
            }

            Thread.Sleep(60);
            return true;
        });
        var (session, _) = Create(connection, () => ValueTask.CompletedTask);

        var table = await TiberoSessionQuery.RunAsync(session, "SELECT N FROM T", null, int.MaxValue, Stall, default).WaitAsync(Quick);

        Assert.Equal(15, table.Rows.Count);
        Assert.Equal(["N"], table.Columns);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
        Assert.False(session.IsStalled);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_slow_statement_before_the_first_row_is_not_measured()
    {
        var connection = new ScriptedConnection(row => row < 2) { ExecuteDelay = TimeSpan.FromMilliseconds(900) };
        var (session, _) = Create(connection, () => ValueTask.CompletedTask);

        var table = await TiberoSessionQuery.RunAsync(session, "SELECT N FROM T", null, int.MaxValue, Stall, default).WaitAsync(Quick);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(SessionState.Idle, session.State);
        await session.DisposeAsync();
    }

    private static bool WaitThenFail(ManualResetEventSlim wake)
    {
        wake.Wait();
        throw new InvalidOperationException("연결이 닫혔다(가짜 드라이버).");
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow + Quick;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "제한 시간 안에 끝나지 않았다.");
            await Task.Delay(10);
        }
    }
}

/// <summary>명령마다 같은 가짜 리더를 주는 연결. 리더의 Read 는 <c>next(행 번호)</c> 가 정한다(막을 수도 있다).</summary>
#pragma warning disable CS8765 // 바탕 형식의 null 허용 표기를 따른다.
internal sealed class ScriptedConnection(Func<int, bool> next) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    /// <summary>ExecuteReader 가 돌아오기 전 기다림(서버가 문장을 도는 시간).</summary>
    public TimeSpan ExecuteDelay { get; init; }

    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => "6";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new ScriptedCommand(this, next);

    private sealed class ScriptedCommand(ScriptedConnection owner, Func<int, bool> next) : DbCommand
    {
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } = new Parameters();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar() => null;

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (owner.ExecuteDelay > TimeSpan.Zero)
            {
                Thread.Sleep(owner.ExecuteDelay); // 드라이버처럼 동기로 기다린다
            }

            return new ScriptedReader(next);
        }
    }

    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<object> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => _items;

        public override int Add(object value)
        {
            _items.Add(value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var v in values)
            {
                _items.Add(v);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf(value);

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => _items.Insert(index, value);

        public override void Remove(object value) => _items.Remove(value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
        }

        protected override DbParameter GetParameter(int index) => (DbParameter)_items[index];

        protected override DbParameter GetParameter(string parameterName) => throw new ArgumentOutOfRangeException(parameterName);

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => throw new ArgumentOutOfRangeException(parameterName);
    }

    /// <summary>열 하나(N). 드라이버처럼 ReadAsync 가 동기 Read 로 돈다(바탕 형식 기본 구현).</summary>
    private sealed class ScriptedReader(Func<int, bool> next) : DbDataReader
    {
        private int _row = -1;

        public override int FieldCount => 1;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(0);

        public override int RecordsAffected => -1;

        public override bool HasRows => true;

        public override bool IsClosed => false;

        public override int Depth => 0;

        public override bool Read()
        {
            if (!next(_row + 1))
            {
                return false;
            }

            _row++;
            return true;
        }

        public override bool NextResult() => false;

        public override string GetName(int ordinal) => "N";

        public override int GetOrdinal(string name) => 0;

        public override object GetValue(int ordinal) => _row;

        public override bool IsDBNull(int ordinal) => false;

        public override string GetDataTypeName(int ordinal) => "NUMBER";

        public override Type GetFieldType(int ordinal) => typeof(int);

        public override int GetValues(object[] values)
        {
            values[0] = _row;
            return 1;
        }

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => _row;

        public override double GetDouble(int ordinal) => _row;

        public override float GetFloat(int ordinal) => _row;

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => (short)_row;

        public override int GetInt32(int ordinal) => _row;

        public override long GetInt64(int ordinal) => _row;

        public override string GetString(int ordinal) => _row.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
#pragma warning restore CS8765
