using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Completion;
using Oranda.Testing;

namespace Oranda.Language.Tests.Completion;

/// <summary>
/// D-041: 자동 완성·매개변수 힌트는 바쁜 세션에서 기다리지 않는다. 조회는 <see cref="SessionWait.Brief"/> 범위에서 시작하고,
/// 바빠서 실패한 조회는 메모리 캐시에 남지 않아 다음 입력 때 다시 시도한다.
/// </summary>
public sealed class BusySessionLookupTests
{
    [Fact]
    public async Task Lookup_starts_inside_brief_scope_and_retries_after_busy()
    {
        var meta = new BusyAwareMetadata { Busy = true };
        var lookup = new MetadataLookup(meta);
        var s = new FakeSession();
        var emp = new DbObjectRef("SCOTT", "EMP", "TABLE");

        await Assert.ThrowsAsync<SessionBusyException>(() => lookup.TableAsync(s, emp));
        Assert.Null(SessionWait.CurrentLimit); // 범위는 부른 쪽으로 새지 않는다
        Assert.False(lookup.TryPeek<TableInfo>(MetadataLookup.TableKey(s, "SCOTT", "EMP"), out _));

        meta.Busy = false;
        var table = await lookup.TableAsync(s, emp); // 바쁜 실패는 기억하지 않는다
        Assert.Equal("EMP", table.Table.Name);
        Assert.All(meta.Limits, l => Assert.Equal(SessionWait.DefaultBrief, l));
    }

    [Fact]
    public async Task Parameter_hints_do_not_wait_for_a_busy_session()
    {
        var meta = new BusyAwareMetadata { Busy = true };

        var found = await ParameterHints.GetSignaturesAsync(meta, new FakeSession(), new CallSite(null, "PKG", "RUN", 0, 0), default);

        Assert.Empty(found);
        Assert.NotEmpty(meta.Limits);
        Assert.All(meta.Limits, l => Assert.Equal(SessionWait.DefaultBrief, l));
        Assert.Null(SessionWait.CurrentLimit);
    }

    /// <summary>즉시성 범위에서 불리면 세션 잠금을 못 잡은 것처럼 바쁨으로 끝난다(실제 잠금은 Oranda.Oracle).</summary>
    private sealed class BusyAwareMetadata : IMetadataProvider
    {
        private readonly FakeMetadataProvider _inner = new();

        public BusyAwareMetadata() =>
            _inner.Tables[new DbObjectRef("SCOTT", "EMP", "TABLE")] = new TableInfo(new DbObjectRef("SCOTT", "EMP", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER")]);

        public bool Busy { get; set; }

        public List<TimeSpan?> Limits { get; } = [];

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => Gate(() => _inner.ListSchemasAsync(s, ct));

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => Gate(() => _inner.ListObjectsAsync(s, q, ct));

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => Gate(() => _inner.GetTableAsync(s, table, ct));

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Gate(() => _inner.GetProceduresAsync(s, obj, ct));

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Gate(() => _inner.GetDdlAsync(s, obj, o, ct));

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Gate(() => _inner.GetObjectDetailAsync(s, obj, ct));

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Gate(() => _inner.GetSourceAsync(s, obj, ct));

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Gate(() => _inner.GetCompileErrorsAsync(s, obj, ct));

        private async Task<T> Gate<T>(Func<Task<T>> next)
        {
            await Task.Yield(); // 실제 제공자처럼 비동기로 이어진 뒤에도 범위를 본다
            Limits.Add(SessionWait.CurrentLimit);
            if (Busy && SessionWait.CurrentLimit is not null)
            {
                throw new SessionBusyException();
            }

            return await next();
        }
    }
}
