using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Tests.Schema;

// 계획 Task 3: Oracle 테이블 편집 방언. 근거 = docs/design/oracle-table-edit-probe.md(26ai·11g 실측).
public class OracleTableEditDialectTests
{
    private static readonly OracleTableEditDialect Dialect = new();

    private static ServerInfo Server(string version, string? edition = "Enterprise") =>
        new(version, null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false), Edition: edition);

    [Fact]
    public void Is_registered_as_the_oracle_table_edit_dialect()
    {
        var attribute = typeof(OracleTableEditDialect).GetCustomAttribute<OrandaServiceAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(typeof(ITableEditDialect), attribute!.ServiceType);
        Assert.Equal(Dbms.Oracle, attribute.ForDbms);
        Assert.Equal(Dbms.Oracle, Dialect.Dbms);
    }

    [Theory]
    [InlineData(null, 30)] // 모르면 작은 쪽
    [InlineData("11.2.0.2.0", 30)] // 실측: 31자 ORA-00972
    [InlineData("12.1.0.2.0", 30)]
    [InlineData("12.2.0.1.0", 128)]
    [InlineData("19.0.0.0.0", 128)]
    [InlineData("23.26.3.0.0", 128)] // 실측: 38자 테이블 이름 됨
    public void Identifier_limit_is_128_bytes_from_12_2(string? version, int expected)
    {
        Assert.Equal(expected, Dialect.MaxIdentifierBytes(version is null ? null : Server(version)));
    }

    [Fact]
    public void Virtual_expression_modify_is_supported_and_drop_keeps_the_recycle_bin()
    {
        Assert.True(Dialect.SupportsVirtualColumnModify); // 실측 1: 11g·26ai 모두 MODIFY (c AS (expr))
        Assert.False(Dialect.PurgesOnDrop);
    }

    [Fact]
    public void Type_list_follows_the_design_and_leaves_out_long()
    {
        Assert.Contains("VARCHAR2", Dialect.TypeNames);
        Assert.Contains("TIMESTAMP WITH TIME ZONE", Dialect.TypeNames);
        Assert.DoesNotContain("LONG", Dialect.TypeNames);
        Assert.Equal(Dialect.TypeNames.Count, Dialect.TypeNames.Distinct().Count());
    }

    [Theory]
    [InlineData(TableEditFeature.IdentityColumn, "11.2.0.2.0", OracleTableEditDialect.Needs12cKey)]
    [InlineData(TableEditFeature.InvisibleColumn, "11.2.0.2.0", OracleTableEditDialect.Needs12cKey)]
    [InlineData(TableEditFeature.IdentityColumn, "19.0.0.0.0", null)]
    [InlineData(TableEditFeature.DomainColumn, "19.0.0.0.0", OracleTableEditDialect.Needs23Key)]
    [InlineData(TableEditFeature.DomainColumn, "23.26.3.0.0", null)]
    [InlineData(TableEditFeature.PartitionConversion, "11.2.0.4.0", null)] // 12.2 미만은 다시 만들기 스크립트(TE-41)
    [InlineData(TableEditFeature.PartitionConversion, "19.0.0.0.0", null)]
    [InlineData(TableEditFeature.VirtualColumnModify, "11.2.0.2.0", null)]
    [InlineData(TableEditFeature.RenameTable, "11.2.0.2.0", null)]
    [InlineData(TableEditFeature.FunctionBasedIndex, "11.2.0.2.0", null)]
    public void Version_gated_features_name_the_needed_release(TableEditFeature feature, string version, string? expected)
    {
        Assert.Equal(expected, Dialect.UnsupportedReasonKey(feature, Server(version)));
    }

    [Theory]
    [InlineData("Express", OracleTableEditDialect.NeedsEnterpriseKey)] // 실측: 11g XE ORA-00439
    [InlineData("Standard", OracleTableEditDialect.NeedsEnterpriseKey)]
    [InlineData("Enterprise", null)]
    [InlineData("Free", null)] // 실측: 26ai Free 는 비트맵·파티션 됨
    [InlineData(null, null)] // 에디션을 모르면 막지 않는다(서버가 알린다)
    public void Bitmap_and_partitions_need_an_edition_that_has_them(string? edition, string? expected)
    {
        Assert.Equal(expected, Dialect.UnsupportedReasonKey(TableEditFeature.BitmapIndex, Server("19.0.0.0.0", edition)));
        Assert.Equal(expected, Dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, Server("19.0.0.0.0", edition)));
    }

    [Fact]
    public void Unknown_server_turns_off_only_the_version_or_edition_dependent_features()
    {
        Assert.Equal(OracleTableEditDialect.UnknownServerKey, Dialect.UnsupportedReasonKey(TableEditFeature.IdentityColumn, null));
        Assert.Equal(OracleTableEditDialect.UnknownServerKey, Dialect.UnsupportedReasonKey(TableEditFeature.BitmapIndex, null));
        Assert.Null(Dialect.UnsupportedReasonKey(TableEditFeature.RenameColumn, null));
    }
}
