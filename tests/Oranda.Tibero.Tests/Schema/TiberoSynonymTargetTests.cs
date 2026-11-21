using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>
/// Tibero 동의어 대상 글 풀기(<see cref="TiberoSynonymTarget"/>): 원격 동의어는 <c>*_synonyms.org_object_name</c> 에 <c>"이름"@링크</c> 로 들어 있다(2026-11-21 실측).
/// 이름 풀이 한 단계·동의어 DDL 이 같은 규칙을 쓴다.
/// </summary>
public class TiberoSynonymTargetTests
{
    [Theory]
    [InlineData("APP", "\"ORDERS\"@REMOTE_DB", "APP", "ORDERS", "REMOTE_DB")] // 실측 모양: 따옴표 이름 + 링크
    [InlineData("APP", "ORDERS@REMOTE_DB", "APP", "ORDERS", "REMOTE_DB")] // 따옴표 없음
    [InlineData("APP", "\"SAMPLE\".\"ORDERS\"@L", "SAMPLE", "ORDERS", "L")] // 이름 글에 소유자가 들어 있으면 그것이 앞선다
    [InlineData("APP", "\"Mixed Case\"@L.EXAMPLE.COM", "APP", "Mixed Case", "L.EXAMPLE.COM")] // 링크 이름의 점은 링크 이름의 일부
    [InlineData("APP", "\"A\"\"B\"@L", "APP", "A\"B", "L")] // 따옴표 안의 "" 는 따옴표 하나
    public void A_remote_target_splits_into_name_and_link(string owner, string raw, string expectedOwner, string expectedName, string expectedLink)
    {
        Assert.Equal(new TiberoSynonymTarget(expectedOwner, expectedName, expectedLink), TiberoSynonymTarget.Parse(owner, raw));
    }

    [Theory]
    [InlineData("SCOTT", "EMP")]
    [InlineData(null, "EMP")]
    [InlineData("SCOTT", "\"MAIL@HOME\"")] // 따옴표 안의 @ 는 링크가 아니다(글은 그대로 둔다)
    public void A_local_target_stays_as_it_is(string? owner, string raw)
    {
        Assert.Equal(new TiberoSynonymTarget(owner, raw, null), TiberoSynonymTarget.Parse(owner, raw));
    }

    [Fact]
    public void A_remote_synonym_ddl_names_the_link_after_the_target()
    {
        Assert.Equal("CREATE OR REPLACE PUBLIC SYNONYM \"ORDERS\" FOR \"APP\".\"ORDERS\"@REMOTE_DB;",
            TiberoDdlAssembler.Synonym("PUBLIC", "ORDERS", "APP", "ORDERS", true, "REMOTE_DB"));
        Assert.Equal("CREATE OR REPLACE SYNONYM \"HR\".\"E\" FOR \"SCOTT\".\"EMP\"", TiberoDdlAssembler.Synonym("HR", "E", "SCOTT", "EMP", false, null));
    }

    [Fact]
    public async Task Name_step_and_ddl_read_the_link_out_of_the_target_name()
    {
        var provider = new TiberoMetadataProvider(Recording([], _ => Table(["KIND", "TABLE_OWNER", "TABLE_NAME", "DB_LINK"], ["SYNONYM", "APP", "\"ORDERS\"@REMOTE_DB", null])));
        await using var session = Session();

        Assert.Equal(NameStep.Synonym("APP", "ORDERS", "REMOTE_DB"), await provider.GetNameStepAsync(session, "PUBLIC", "ORDERS", CancellationToken.None));
        Assert.Equal("CREATE OR REPLACE PUBLIC SYNONYM \"ORDERS\" FOR \"APP\".\"ORDERS\"@REMOTE_DB",
            await provider.GetDdlAsync(session, new DbObjectRef("PUBLIC", "ORDERS", "SYNONYM"), new DdlOptions { SqlTerminator = false }, CancellationToken.None));
    }
}
