using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

// 소유: C6. DBMS 별 기능 지원 표시(docs/design/tibero.md 2장·3장). Oracle 동작은 그대로여야 한다.
public class DbmsSupportTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(19)]
    [InlineData(23)]
    public void Oracle_features_by_dbms_equal_the_version_only_overload(int major)
        => Assert.Equal(ServerFeatures.For(major), ServerFeatures.For(major, Dbms.Oracle));

    [Fact]
    public void Tibero_version_features_follow_the_6_2_15_measurements()
    {
        // 실측(docs/design/tibero-catalog-map.md): FETCH FIRST·OFFSET … FETCH 됨, 나머지 세 칸은 근거 없음/없음 확인 → 끔.
        foreach (var major in new[] { 6, 7 })
        {
            var f = ServerFeatures.For(major, Dbms.Tibero);

            Assert.True(f.RowLimiting);
            Assert.False(f.Release12Columns);
            Assert.False(f.Multitenant);
            Assert.False(f.Release23Types);
            Assert.Equal(Dbms.Tibero, f.Dbms);
        }

        var tibero = ServerFeatures.For(6, Dbms.Tibero);
        Assert.Equal("Tibero 에서는 지원하지 않음", tibero.UnavailableReason);
    }

    [Fact]
    public void Oracle_row_limiting_still_needs_12c()
    {
        Assert.False(ServerFeatures.For(11).RowLimiting);
        Assert.True(ServerFeatures.For(12).RowLimiting);
        Assert.Equal(ServerFeatures.UnavailableText, ServerFeatures.For(11).UnavailableReason);
        Assert.Equal(ServerFeatures.UnavailableText, ServerFeatures.For(19).UnavailableReason);
    }

    // 기능마다 한 행(tibero.md 3장 + tibero-catalog-map.md 6.2.15 실측). 실측으로 한 행을 고치면 이 행만 바뀐다. 처음 7행은 아직 끔(근거 부족),
    // 뒤 5행(P0-b)은 원본이 없어 끔(t4-decision Q2, tibero-monitor-catalog.md).
    [Theory]
    [InlineData(DbmsFeature.SqlMonitorReport, true, false)]
    [InlineData(DbmsFeature.AwrReport, true, false)]
    [InlineData(DbmsFeature.Ash, true, false)]
    [InlineData(DbmsFeature.SessionTrace, true, false)]
    [InlineData(DbmsFeature.Tkprof, true, false)]
    [InlineData(DbmsFeature.OptimizerTrace, true, false)]
    [InlineData(DbmsFeature.JavaSource, true, false)]
    [InlineData(DbmsFeature.SharedCursorReasons, true, false)]
    [InlineData(DbmsFeature.PlanControls, true, false)]
    [InlineData(DbmsFeature.Deadlocks, true, false)]
    [InlineData(DbmsFeature.LiteralGroups, true, false)]
    [InlineData(DbmsFeature.SqlMonitorReportHtml, true, false)]
    public void Each_feature_has_its_own_row_per_dbms(DbmsFeature feature, bool oracle, bool tibero)
    {
        Assert.Equal(oracle, DbmsSupport.Supports(Dbms.Oracle, feature));
        Assert.Equal(tibero, DbmsSupport.Supports(Dbms.Tibero, feature));
        Assert.Equal(oracle ? null : "Oracle 에서는 지원하지 않음", DbmsSupport.UnsupportedReason(Dbms.Oracle, feature));
        Assert.Equal(tibero ? null : "Tibero 에서는 지원하지 않음", DbmsSupport.UnsupportedReason(Dbms.Tibero, feature));
        Assert.Equal(oracle, ServerFeatures.For(19).Supports(feature));
        Assert.Equal(tibero, ServerFeatures.For(7, Dbms.Tibero).Supports(feature));
        Assert.Equal(tibero ? null : "Tibero 에서는 지원하지 않음", ServerFeatures.For(7, Dbms.Tibero).UnsupportedReason(feature));
    }

    [Fact]
    public void Every_feature_value_has_a_row_and_unknown_values_throw()
    {
        foreach (var feature in Enum.GetValues<DbmsFeature>())
        {
            _ = DbmsSupport.Supports(Dbms.Oracle, feature);
            _ = DbmsSupport.Supports(Dbms.Tibero, feature);
        }

        Assert.Equal("Oracle 에서는 지원하지 않음", DbmsSupport.NotSupportedText(Dbms.Oracle));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbmsSupport.Supports((Dbms)99, DbmsFeature.Ash));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbmsSupport.Supports(Dbms.Oracle, (DbmsFeature)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbmsSupport.Supports(Dbms.Tibero, (DbmsFeature)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbmsSupport.NotSupportedText((Dbms)99));
    }

    [Fact]
    public void ServerInfo_capabilities_follow_the_server_dbms_even_when_the_provider_left_them_as_oracle()
    {
        var oracleCaps = ServerCapabilities.From(Set("ALTER SYSTEM"), Set("DBA"), true, true); // DBMS 를 넘기지 않음
        var tibero = new ServerInfo("6.2", null, null, null, null, null, false, 1, oracleCaps, Dbms: Dbms.Tibero);
        var oracle = new ServerInfo("19.3", null, null, null, null, null, false, 1, oracleCaps);

        Assert.Equal(Dbms.Tibero, tibero.Capabilities.Dbms);
        Assert.False(tibero.Capabilities.CanTraceSessions);
        Assert.True(tibero.Capabilities.IsDba);
        Assert.True(tibero.Capabilities.CanKillSession);

        Assert.Same(oracleCaps, oracle.Capabilities); // Oracle 은 그대로
        Assert.True(oracle.Capabilities.CanTraceSessions);
        Assert.True((oracle with { Dbms = Dbms.Tibero }).Capabilities.Dbms == Dbms.Tibero);
        Assert.Same(oracleCaps, oracleCaps.WithDbms(Dbms.Oracle));
    }

    [Fact]
    public void Oracle_with_unknown_version_is_not_12c_but_gets_the_latest_features()
    {
        var caps = new ServerCapabilities(false, false, false, false, false);
        var unknown = new ServerInfo("0", null, null, null, null, null, false, 1, caps);

        Assert.Equal(0, unknown.MajorVersion);
        Assert.False(unknown.IsAtLeast12c); // 이전과 같음(MajorVersion >= 12)
        Assert.Equal(ServerFeatures.For(0), unknown.Features);
        Assert.True(unknown.Features.RowLimiting);
    }

    [Fact]
    public void ServerInfo_follows_the_dbms_for_features_and_12c()
    {
        var caps = new ServerCapabilities(false, false, false, false, false);
        var tibero = new ServerInfo("7.2", null, null, null, null, null, false, 1, caps, Dbms: Dbms.Tibero);
        var oracle = new ServerInfo("19.3", null, null, null, null, null, false, 1, caps);

        Assert.False(tibero.IsAtLeast12c);
        Assert.Equal(Dbms.Tibero, tibero.Features.Dbms);
        Assert.False(tibero.Features.Release12Columns);

        Assert.True(oracle.IsAtLeast12c);
        Assert.Equal(Dbms.Oracle, oracle.Features.Dbms);
        Assert.Equal(ServerFeatures.For(19), oracle.Features);
    }

    [Fact]
    public void Tibero_cannot_trace_sessions_even_with_dbms_monitor_execute()
    {
        var tibero = ServerCapabilities.From(Set("ALTER SYSTEM"), Set("DBA"), true, true, Dbms.Tibero);
        var oracle = ServerCapabilities.From(Set("ALTER SYSTEM"), Set("DBA"), true, true);

        Assert.False(tibero.CanTraceSessions);
        Assert.True(tibero.IsDba);
        Assert.True(tibero.CanKillSession);
        Assert.Equal(Dbms.Tibero, tibero.Dbms);
        Assert.Equal("Tibero 에서는 지원하지 않음", tibero.UnsupportedReason(DbmsFeature.SessionTrace));

        Assert.True(oracle.CanTraceSessions);
        Assert.Equal(Dbms.Oracle, oracle.Dbms);
        Assert.Null(oracle.UnsupportedReason(DbmsFeature.SessionTrace));
    }
}
