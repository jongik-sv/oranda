using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>결과 탭 머리의 실행 시간(「결과 1 (3.2s)」).</summary>
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ResultTabElapsedTests
{
    [Fact]
    public Task Query_tab_header_shows_the_elapsed_time_but_the_title_stays() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel();
        var tab = tabs.AddQuery(new OneColumnResult("1"), "결과 1", null, elapsed: TimeSpan.FromMilliseconds(3200));

        Assert.Equal("결과 1", tab.Title); // 번호 찾기·「(편집)」 표시는 제목을 그대로 쓴다
        Assert.Equal("결과 1 (3.2s)", tab.Header);
        Assert.Equal(2, tabs.NextOrdinal());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Tab_without_elapsed_time_shows_the_title_only() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel();
        var tab = tabs.AddQuery(new OneColumnResult("1"), "부모: DEPT", null);

        Assert.Equal("부모: DEPT", tab.Header);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Header_follows_title_changes() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel();
        var tab = tabs.AddQuery(new OneColumnResult("1"), "결과 1", null, elapsed: TimeSpan.FromSeconds(1.26));
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Title = "결과 1 (편집)";

        Assert.Contains(nameof(ResultTabItem.Header), changed);
        Assert.Equal("결과 1 (편집) (1.3s)", tab.Header);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(12, "0.01s")]
    [InlineData(850, "0.85s")]
    [InlineData(3200, "3.2s")]
    [InlineData(59_940, "59.9s")]
    [InlineData(65_000, "1m 05s")]
    [InlineData(3_665_000, "1h 01m 05s")]
    public void Elapsed_time_is_short(int milliseconds, string expected) =>
        Assert.Equal(expected, ResultTabItem.FormatElapsed(TimeSpan.FromMilliseconds(milliseconds)));
}
