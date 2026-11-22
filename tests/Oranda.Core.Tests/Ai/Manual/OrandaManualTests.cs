using Oranda.Core.Ai.Manual;

namespace Oranda.Core.Tests.Ai.Manual;

/// <summary><c>orandactl docs</c> 검색(<see cref="OrandaManual.Find(string?, IReadOnlyList{OrandaManual.Entry}, int)"/>)과 h2 앵커.</summary>
public sealed class OrandaManualTests
{
    private static readonly OrandaManual.Entry[] Entries =
    [
        new("SQL 튜닝", "튜닝 메뉴의 도구로 느린 SQL의 원인을 찾습니다.", OrandaManual.BaseUrl + "/plan/tuning/", "plan")
        {
            Headings = ["통계 보기", "AI 튜닝 시작 창"],
        },
        new("데이터 내보내기와 가져오기", "결과를 파일로 내보냅니다.", OrandaManual.BaseUrl + "/transfer/data-transfer/", "transfer")
        {
            Headings = ["엑셀로 내보내기", "튜닝 결과 붙여 넣기"],
        },
    ];

    [Fact]
    public void Page_matches_come_before_heading_only_matches_and_carry_the_matched_headings()
    {
        var hits = OrandaManual.Find("튜닝", Entries);

        Assert.Equal(["SQL 튜닝", "데이터 내보내기와 가져오기"], hits.Select(h => h.Entry.Title));
        Assert.Equal(["AI 튜닝 시작 창"], hits[0].Headings);
        Assert.Equal(["튜닝 결과 붙여 넣기"], hits[1].Headings);
    }

    [Fact]
    public void Every_word_must_appear_somewhere_in_the_page()
    {
        Assert.Equal("데이터 내보내기와 가져오기", Assert.Single(OrandaManual.Find("엑셀 transfer", Entries)).Entry.Title);
        Assert.Empty(OrandaManual.Find("엑셀 통계", Entries));
    }

    [Fact]
    public void Empty_query_lists_pages_without_headings()
    {
        var hits = OrandaManual.Find("  ", Entries);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Empty(h.Headings));
        Assert.Single(OrandaManual.Find(null, Entries, limit: 1));
    }

    [Theory]
    [InlineData("접속의 AI 허용 수준", "접속의-ai-허용-수준")]
    [InlineData("설치 없이 쓰기 (Portable)", "설치-없이-쓰기-portable")]
    [InlineData("닫기, 끝내기, 복구", "닫기-끝내기-복구")]
    [InlineData("SQL*Plus 명령", "sqlplus-명령")]
    public void Anchor_follows_the_starlight_heading_ids(string heading, string anchor)
    {
        Assert.Equal(anchor, OrandaManual.Anchor(heading));
        Assert.EndsWith("/plan/tuning/#" + anchor, Entries[0].HeadingUrl(heading), StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_index_has_the_external_agents_page_and_the_manual_home()
    {
        Assert.Contains(OrandaManual.All, e => e.Url == OrandaManual.BaseUrl + "/ai/external-agents/" && e.Section == "ai" && e.Headings.Count > 0);
        Assert.Contains(OrandaManual.All, e => e.Url == OrandaManual.BaseUrl + "/manual/" && e.Section == "manual");
        Assert.Equal(OrandaManual.All.Count, OrandaManual.All.Select(e => e.Url).Distinct(StringComparer.Ordinal).Count());
    }
}
