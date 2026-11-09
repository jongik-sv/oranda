using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Testing;

namespace Oranda.Core.Tests.Composition;

// 소유: tb-core C3(D-046). DBMS 키 등록 표시와 DBMS 별 구현 고르기.

public class DbmsServiceSelectorTests
{
    [Fact]
    public void Attribute_without_dbms_is_dbms_neutral_and_with_dbms_is_keyed()
    {
        var neutral = new OrandaServiceAttribute(typeof(IGreeter));
        var keyed = new OrandaServiceAttribute(typeof(IGreeter)) { Dbms = Dbms.Tibero };

        Assert.Null(neutral.ForDbms);
        Assert.Equal(Dbms.Tibero, keyed.ForDbms);
        Assert.Equal(Dbms.Oracle, new OrandaServiceAttribute(typeof(IGreeter)) { Dbms = Dbms.Oracle }.ForDbms); // 기본값과 같아도 「달았음」
    }

    [Fact]
    public void Concurrent_first_use_builds_the_singleton_once()
    {
        var built = 0;
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () =>
        {
            Interlocked.Increment(ref built);
            Thread.Sleep(20);
            return new OracleGreeter();
        });

        var results = new IGreeter[8];
        Parallel.For(0, results.Length, i => results[i] = table.Get<IGreeter>(Dbms.Oracle));

        Assert.Equal(1, built);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public void Attribute_dbms_key_is_read_from_class_metadata()
    {
        var attribute = Assert.Single(typeof(TiberoGreeter).GetCustomAttributes(typeof(OrandaServiceAttribute), false).Cast<OrandaServiceAttribute>());

        Assert.Equal(Dbms.Tibero, attribute.ForDbms);
    }

    [Fact]
    public void Table_picks_implementation_by_dbms_and_lists_available_dbms()
    {
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () => new OracleGreeter());

        Assert.Equal("oracle", table.Get<IGreeter>(Dbms.Oracle).Hello());
        Assert.Null(table.Find<IGreeter>(Dbms.Tibero));
        Assert.Equal([Dbms.Oracle], table.AvailableDbms);

        table.Add<IGreeter>(Dbms.Tibero, () => new TiberoGreeter());

        Assert.Equal("tibero", table.Get<IGreeter>(Dbms.Tibero).Hello());
        Assert.Equal([Dbms.Oracle, Dbms.Tibero], table.AvailableDbms);
    }

    [Fact]
    public void Implementation_is_built_once_on_first_use()
    {
        var built = 0;
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () =>
        {
            built++;
            return new OracleGreeter();
        });
        Assert.Equal(0, built);

        Assert.Same(table.Get<IGreeter>(Dbms.Oracle), table.Get<IGreeter>(Dbms.Oracle));
        Assert.Equal(1, built);
    }

    [Fact]
    public void Session_dbms_selects_the_implementation_and_missing_one_throws()
    {
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () => new OracleGreeter());
        var oracle = new FakeSession(TestData.Profile());
        var tibero = new FakeSession(TestData.Profile() with { Dbms = Dbms.Tibero });

        Assert.Equal("oracle", table.For<IGreeter>(oracle).Hello());
        var ex = Assert.Throws<DbmsNotSupportedException>(() => table.For<IGreeter>(tibero));
        Assert.Equal(Dbms.Tibero, ex.Dbms);
        Assert.Equal(typeof(IGreeter), ex.ServiceType);
    }

    [Fact]
    public void Unset_attribute_key_reads_oracle_only_through_the_setter_property()
    {
        var neutral = new OrandaServiceAttribute(typeof(IGreeter));

        Assert.Equal(Dbms.Oracle, neutral.Dbms); // 함정: 읽기는 ForDbms 로
        Assert.Null(neutral.ForDbms);
    }

    [Fact]
    public void Ensure_loaded_runs_before_answering_so_deferred_providers_appear()
    {
        DbmsServiceTable? table = null;
        var scans = 0;
        table = new DbmsServiceTable(() =>
        {
            if (scans++ == 0)
            {
                table!.Add<IGreeter>(Dbms.Tibero, () => new TiberoGreeter());
            }
        });

        Assert.Equal([Dbms.Tibero], table.AvailableDbms);
        Assert.Equal("tibero", table.Get<IGreeter>(Dbms.Tibero).Hello());
    }

    [Fact]
    public void Transient_registration_builds_each_time()
    {
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () => new OracleGreeter(), ServiceLifetimeKind.Transient);

        Assert.NotSame(table.Get<IGreeter>(Dbms.Oracle), table.Get<IGreeter>(Dbms.Oracle));
    }

    [Fact]
    public void Failing_singleton_factory_keeps_failing()
    {
        var calls = 0;
        var table = new DbmsServiceTable().Add<IGreeter>(Dbms.Oracle, () =>
        {
            calls++;
            throw new InvalidOperationException("bad config");
        });

        Assert.Throws<InvalidOperationException>(() => table.Find<IGreeter>(Dbms.Oracle));
        Assert.Throws<InvalidOperationException>(() => table.Find<IGreeter>(Dbms.Oracle));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Later_registration_for_the_same_key_wins()
    {
        var table = new DbmsServiceTable()
            .Add<IGreeter>(Dbms.Oracle, () => new OracleGreeter())
            .Add<IGreeter>(Dbms.Oracle, () => new TiberoGreeter());

        Assert.Equal("tibero", table.Get<IGreeter>(Dbms.Oracle).Hello());
    }

    [Fact]
    public void Factory_of_the_wrong_type_is_reported()
    {
        var table = new DbmsServiceTable().Add(typeof(IGreeter), Dbms.Oracle, () => new object());

        Assert.Throws<InvalidOperationException>(() => table.Find<IGreeter>(Dbms.Oracle));
    }

    internal interface IGreeter
    {
        string Hello();
    }

    private sealed class OracleGreeter : IGreeter
    {
        public string Hello() => "oracle";
    }

    [OrandaService(typeof(IGreeter), Dbms = Dbms.Tibero)]
    private sealed class TiberoGreeter : IGreeter
    {
        public string Hello() => "tibero";
    }
}
