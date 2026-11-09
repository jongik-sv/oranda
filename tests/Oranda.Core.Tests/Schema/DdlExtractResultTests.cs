using Oranda.Core.Metadata;
using Oranda.Core.Schema;

namespace Oranda.Core.Tests.Schema;

// 소유: tb-core C4. DDL 일괄 추출 결과 집계.

public class DdlExtractResultTests
{
    [Fact]
    public void Counts_split_items_into_succeeded_and_failed()
    {
        var result = new DdlExtractResult(
            [
                new DdlExtractItem(new DbObjectRef("SCOTT", "EMP", "TABLE"), true, null),
                new DdlExtractItem(new DbObjectRef("SCOTT", "DEPT", "TABLE"), true, null),
                new DdlExtractItem(new DbObjectRef("SCOTT", "BAD", "VIEW"), false, "denied"),
            ],
            []);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
    }
}
