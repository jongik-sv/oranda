using System.Data;
using System.Data.Common;

namespace Oranda.Tibero.Tests.Sessions;

/// <summary>서버 없이 세션을 시험하는 최소 연결. 명령을 보내지 않고, 상태 바뀜(<see cref="DbConnection.StateChange"/>)만 흉내 낸다.</summary>
#pragma warning disable CS8765 // ConnectionString 의 null 허용 표기는 바탕 형식을 따른다.
internal sealed class FakeDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    public int DisposeCount { get; private set; }

    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => "6";

    public override ConnectionState State => _state;

    /// <summary>드라이버가 연결을 잃은 것처럼 Closed 로 바꾸고 알린다.</summary>
    public void Drop()
    {
        var before = _state;
        _state = ConnectionState.Closed;
        OnStateChange(new StateChangeEventArgs(before, ConnectionState.Closed));
    }

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException("가짜 연결은 명령을 보내지 않는다.");

    protected override void Dispose(bool disposing)
    {
        DisposeCount++;
        base.Dispose(disposing);
    }
}
#pragma warning restore CS8765

/// <summary>드라이버 결함 흉내: 정리(<see cref="DbTransaction.Dispose()"/>)가 NullReferenceException 을 던지는 트랜잭션(실측 6.2.15, 닫힌 연결).</summary>
internal sealed class ThrowingTransaction(DbConnection connection) : DbTransaction
{
    public int DisposeCalls { get; private set; }

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection DbConnection => connection;

    public override void Commit() => throw new NotSupportedException();

    public override void Rollback() => throw new NotSupportedException();

    // 드라이버처럼 비어 있는 내부 연결 필드를 읽다 실제 NullReferenceException 이 난다.
    private static string? DriverConnectionField => null;

    protected override void Dispose(bool disposing)
    {
        DisposeCalls++;
        try
        {
            _ = DriverConnectionField!.Length;
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
