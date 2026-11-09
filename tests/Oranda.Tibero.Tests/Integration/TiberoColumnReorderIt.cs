using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 열 순서 바꾸기의 표 종류·참조 소스 줄 조회를 실제 서버로 읽는다(읽기 전용). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 접속 계정 이름이 든 값은 참·거짓만 본다(Assert.Equal·Contains·All 은 실패 때 값을 찍는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoColumnReorderIt
{
    private const string FirstOwnTable =
        "SELECT table_name FROM (SELECT table_name FROM user_tables WHERE table_name NOT LIKE 'BIN$%' ORDER BY table_name) WHERE ROWNUM = 1";

    /// <summary>접속 계정의 표 하나의 종류를 읽는다(질의가 오류 없이 돈다). 계정에 표가 없으면 건너뛴다.</summary>
    [TiberoItFact]
    public async Task Traits_of_a_table_in_the_login_schema_read_without_an_error_and_are_not_clustered()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var name = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, FirstOwnTable));
        if (name is null)
        {
            return;
        }

        var owner = it.Env.User.ToUpperInvariant();
        var traits = await new TiberoColumnReorderProvider().GetTraitsAsync(session, new DbObjectRef(owner, name, "TABLE"), CancellationToken.None);

        // Tibero 는 클러스터를 알아내지 못하고 늘 비클러스터다. 외부·임시 표 여부는 표에 따라 다르다.
        Assert.True(!traits.IsClustered, "cluster_name 은 늘 NULL 이어야 한다");
    }

    /// <summary>없는 표는 모든 종류가 꺼진 값을 돌려주고, 참조 소스 줄은 빈 목록이다.</summary>
    [TiberoItFact]
    public async Task A_missing_table_has_no_traits_and_no_referencing_source_lines()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var provider = new TiberoColumnReorderProvider();
        var missing = new DbObjectRef(it.Env.User.ToUpperInvariant(), "ORT_NO_SUCH_TABLE_D4", "TABLE");

        var traits = await provider.GetTraitsAsync(session, missing, CancellationToken.None);
        var lines = await provider.FindReferencingSourceLinesAsync(session, missing, CancellationToken.None);

        Assert.True(!traits.IsExternal && !traits.IsTemporary && !traits.IsClustered, "없는 표인데 종류가 켜져 있다");
        Assert.True(lines.Count == 0, "없는 표인데 참조 소스 줄이 왔다");
    }

    /// <summary>접속 계정 표를 참조하는 소스 줄 조회가 오류 없이 돌고 상한·줄 번호·줄 끝 모양을 지킨다(없으면 빈 목록이어도 된다).</summary>
    [TiberoItFact]
    public async Task Referencing_source_lines_of_a_login_schema_table_read_within_the_limit()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var name = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, FirstOwnTable));
        if (name is null)
        {
            return;
        }

        var lines = await new TiberoColumnReorderProvider().FindReferencingSourceLinesAsync(
            session, new DbObjectRef(it.Env.User.ToUpperInvariant(), name, "TABLE"), CancellationToken.None);

        Assert.True(lines.Count <= TiberoColumnReorderProvider.MaxSourceLines, "max_rows 를 넘었다");
        Assert.True(lines.All(l => l.Line >= 1 && l.Ref.Name.Length > 0 && !l.Text.EndsWith('\n')), "줄 번호·이름·줄 끝 모양이 어긋난다");
    }
}
