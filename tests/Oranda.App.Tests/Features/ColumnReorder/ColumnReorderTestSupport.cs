using System.Runtime.CompilerServices;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ColumnReorder;

/// <summary>TSK-13-01 시험용 가짜 세션·메타데이터·실행 엔진.</summary>
internal sealed class ReorderSession(string version = "19.0.0.0", string? edition = "Enterprise", bool production = false) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = new("t", "SCOTT", "localhost/FREEPDB1", Id: Guid.NewGuid())
    {
        Environment = production ? ConnectionEnvironment.Production : ConnectionEnvironment.Development,
    };

    public ServerInfo Server { get; } = new(version, "FREE", "h", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, true, true, false, false), Edition: edition);

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey => "FREE/FREEPDB1/SCOTT";

    public SessionState State => SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ReorderMeta : IMetadataProvider
{
    public static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    /// <summary>서버가 돌려주는 열(바뀌면 다음 읽기에 반영된다).</summary>
    public List<ColumnInfo> Columns { get; set; } =
    [
        new("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
        new("NAME", 2, "VARCHAR2") { Length = 30 },
        new("HIRED", 3, "DATE"),
        new("SAL", 4, "NUMBER") { Precision = 8, Scale = 2 },
    ];

    public int GetTableCalls { get; private set; }

    public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        GetTableCalls++;
        return Task.FromResult(new TableInfo(table, [.. Columns])
        {
            Constraints = [new ConstraintInfo("EMP_PK", ConstraintKind.PrimaryKey, ["ID"])],
        });
    }

    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
        => Task.FromResult(o.IncludeDependentObjects
            ? "CREATE TABLE \"SCOTT\".\"EMP\" (ID NUMBER);\nCREATE UNIQUE INDEX \"SCOTT\".\"EMP_PK\" ON \"SCOTT\".\"EMP\" (\"ID\");"
            : "CREATE TABLE \"SCOTT\".\"EMP\" (ID NUMBER);");

    public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        => Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, [new DetailSection("열", ["이름"], [["ID"]])]));

    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class ReorderProvider : IColumnReorderProvider
{
    public ColumnReorderTraits Traits { get; set; } = new(false, false, null);

    public bool TraitsNotFound { get; set; }

    public List<SourceSearchHit> Lines { get; set; } = [];

    public Exception? ScanFails { get; set; }

    public int ScanCalls { get; private set; }

    public Task<ColumnReorderTraits> GetTraitsAsync(ISession s, DbObjectRef table, CancellationToken ct)
        => TraitsNotFound
            ? Task.FromException<ColumnReorderTraits>(new CatalogQueryNotFoundException("dbinfo.column_reorder_traits", 11))
            : Task.FromResult(Traits);

    public Task<IReadOnlyList<SourceSearchHit>> FindReferencingSourceLinesAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ScanCalls++;
        return ScanFails is { } ex ? Task.FromException<IReadOnlyList<SourceSearchHit>>(ex) : Task.FromResult<IReadOnlyList<SourceSearchHit>>(Lines);
    }
}

internal sealed class ReorderExec : IExecutionService
{
    public List<string> Ran { get; } = [];

    /// <summary>이 순번(0부터)의 문장에서 실패한다. null 이면 모두 성공.</summary>
    public int? FailAt { get; set; }

    public ExecutionStatus FailStatus { get; set; } = ExecutionStatus.Failed;

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(
        ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        for (var i = 0; i < stmts.Count; i++)
        {
            Ran.Add(stmts[i].Text);
            if (FailAt == i)
            {
                yield return new ExecutionResult(stmts[i], FailStatus) { Error = new DbError("ORA-54039", "table has no column") };
                yield break;
            }

            yield return new ExecutionResult(stmts[i], ExecutionStatus.Succeeded);
        }
    }
}
