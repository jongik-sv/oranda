using System.Data;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 변경 추적 질의(표 VERSIONS BETWEEN …)는 ODP 의 스키마 표 조회가 보조 질의로 실패해도 본 결과를 보여 줘야 한다. DB 없이 조회 실패만 흉내 낸다.
public class OracleValuesSchemaTests
{
    [Fact]
    public void Failed_schema_lookup_is_skipped_so_result_still_shows()
    {
        var schema = OracleValues.SchemaOrNull(() => throw new InvalidOperationException("ORA-00942: table or view does not exist"));

        Assert.Null(schema);
    }

    [Fact]
    public void Successful_schema_lookup_is_returned()
    {
        using var table = new DataTable();

        Assert.Same(table, OracleValues.SchemaOrNull(() => table));
    }
}
