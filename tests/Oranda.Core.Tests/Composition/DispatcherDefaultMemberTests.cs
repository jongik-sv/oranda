using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.Core.Tests.Composition;

// 디스패처가 인터페이스의 기본 구현(DIM) 멤버를 빠뜨리면 DBMS별 공급자의 재정의가 호출되지 않고 기본값(빈 사전 등)이 나온다.

public class DispatcherDefaultMemberTests
{
    public static TheoryData<Type> DispatcherTypes()
    {
        var data = new TheoryData<Type>();
        var types = typeof(MetadataProviderDispatcher).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace == typeof(MetadataProviderDispatcher).Namespace && t.Name.EndsWith("Dispatcher", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in types)
        {
            data.Add(t);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DispatcherTypes))]
    public void Dispatcher_implements_every_member_of_its_interfaces_explicitly(Type dispatcher)
    {
        var missing = new List<string>();
        foreach (var iface in dispatcher.GetInterfaces())
        {
            var map = dispatcher.GetInterfaceMap(iface);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.TargetMethods[i].DeclaringType != dispatcher)
                {
                    missing.Add($"{iface.Name}.{map.InterfaceMethods[i].Name}");
                }
            }
        }

        Assert.True(missing.Count == 0, $"{dispatcher.Name} 이 기본 구현에 기대는 멤버: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Metadata_dispatcher_forwards_object_marks_to_the_provider_of_the_session_dbms()
    {
        var key = new DbObjectRef("SCOTT", "T1", "TABLE");
        var oracle = new MarksProvider(new ObjectMark("PARTITIONED", null));
        var tibero = new MarksProvider(new ObjectMark(null, "INVALID"));
        var table = new DbmsServiceTable()
            .Add<IMetadataProvider>(Dbms.Oracle, () => oracle)
            .Add<IMetadataProvider>(Dbms.Tibero, () => tibero);
        IMetadataProvider dispatcher = new MetadataProviderDispatcher(table); // 앱처럼 인터페이스로 부른다

        var marks = await dispatcher.ListObjectMarksAsync(new FakeSession(TestData.Profile() with { Dbms = Dbms.Tibero }), "SCOTT", default);

        Assert.Equal("INVALID", marks[key].State);
        Assert.Equal(["SCOTT"], tibero.Owners);
        Assert.Empty(oracle.Owners);
    }

    private sealed class MarksProvider(ObjectMark mark) : IMetadataProvider
    {
        public List<string> Owners { get; } = [];

        public Task<IReadOnlyDictionary<DbObjectRef, ObjectMark>> ListObjectMarksAsync(ISession s, string owner, CancellationToken ct)
        {
            Owners.Add(owner);
            return Task.FromResult<IReadOnlyDictionary<DbObjectRef, ObjectMark>>(
                new Dictionary<DbObjectRef, ObjectMark> { [new DbObjectRef("SCOTT", "T1", "TABLE")] = mark });
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
