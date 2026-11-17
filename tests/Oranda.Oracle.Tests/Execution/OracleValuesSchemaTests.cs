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
    public void Stale_cached_schema_is_skipped_so_result_still_shows()
    {
        // ODP 가 캐시한 옛 열 정보로 스키마 표를 풀다 나는 예외(열을 더한 뒤 같은 표를 다시 읽을 때)
        var schema = OracleValues.SchemaOrNull(() => Array.Empty<DataTable>()[0]);

        Assert.Null(schema);
    }

    [Fact]
    public void Successful_schema_lookup_is_returned()
    {
        using var table = new DataTable();

        Assert.Same(table, OracleValues.SchemaOrNull(() => table));
    }
}
