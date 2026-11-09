using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>DB 정보 구역을 실제 서버로 읽는다(읽기 문장만). 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoDatabaseInfoIt
{
    [TiberoItFact]
    public async Task Every_section_reads_without_an_error_row()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);

        var sections = await new TiberoDatabaseInfoProvider().GetAsync(session, CancellationToken.None);

        Assert.Equal(6, sections.Count);
        // 실패 구역은 열 「오류」 하나다. 제목만 찍는다(메시지에 서버 값이 실릴 수 있다).
        Assert.Empty(sections.Where(x => x.Columns is ["오류"]).Select(x => x.Title));
        // 행에 호스트·DB 이름·계정이 있으므로 참·거짓만 본다(Assert.Contains·All 은 실패 때 모음을 찍는다).
        Assert.Empty(sections.Where(x => x.Title != "옵션" && x.Rows.Count == 0).Select(x => x.Title));
        var hasInstanceName = sections[0].Rows.Any(r => Equals(r[0], "instance_name") && r[1] is not null);
        Assert.True(hasInstanceName, "인스턴스 구역에 instance_name 값이 없다");
    }
}
