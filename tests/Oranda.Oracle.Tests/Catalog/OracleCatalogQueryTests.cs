using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog.Providers;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: tb-base P0b(리뷰 지적). 카탈로그 바인드 → OracleParameter 형이 옮기기 전 AddText·AddInt·AddLong·TopSqlNumber 와 같아야 한다.
// 형이 바뀌면(예: Int32 → Int64) 단위 시험이 아니라 실서버에서만 드러나므로 여기서 고정한다. 연결은 열지 않는다.
public class OracleCatalogQueryTests
{
    [Theory]
    [InlineData(CatalogBindKind.Text, OracleDbType.Varchar2)]
    [InlineData(CatalogBindKind.Int32, OracleDbType.Int32)]
    [InlineData(CatalogBindKind.Int64, OracleDbType.Int64)]
    [InlineData(CatalogBindKind.Double, OracleDbType.Double)]
    [InlineData(CatalogBindKind.Date, OracleDbType.Date)]
    public void Bind_kind_maps_to_the_same_oracle_type_as_before(CatalogBindKind kind, OracleDbType expected)
    {
        Assert.Equal(expected, OracleCatalogQuery.DbType(kind));
    }

    [Fact]
    public void Binds_keep_name_order_input_direction_and_null_as_dbnull()
    {
        using var cmd = new OracleCommand();
        CatalogBind[] binds =
        [
            new("sql_id", "abc", CatalogBindKind.Text),
            new("inst_id", null, CatalogBindKind.Int32),
            new("min_gets", 10L, CatalogBindKind.Int64),
        ];

        OracleCatalogQuery.AddBinds(cmd, binds);

        Assert.Equal(["sql_id", "inst_id", "min_gets"], cmd.Parameters.Cast<OracleParameter>().Select(p => p.ParameterName));
        Assert.All(cmd.Parameters.Cast<OracleParameter>(), p => Assert.Equal(System.Data.ParameterDirection.Input, p.Direction));
        Assert.Equal(OracleDbType.Int32, cmd.Parameters[1].OracleDbType);
        Assert.Equal(DBNull.Value, cmd.Parameters[1].Value);
        Assert.Equal(10L, cmd.Parameters[2].Value);
    }
}
