using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Navigation;
using Oranda.App.Tests.Features.ColumnReorder;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary><c>reveal_object</c> 안쪽: 트리에서 찾기만 하거나, 열기까지.</summary>
public class ExternalObjectRevealerTests
{
    [Fact]
    public async Task Reveal_opens_only_when_asked_and_reports_whether_the_tree_found_it()
    {
        var opened = new List<DbObjectRef>();
        var navigation = new ObjectNavigationCommandHandler
        {
            BrowserProvider = () => null, // 스키마 브라우저가 없으면 트리에서 찾지 못한다
            OpenObject = (target, _, _) =>
            {
                opened.Add(target);
                return Task.CompletedTask;
            },
        };
        var revealer = new ExternalObjectRevealer(navigation);
        var target = new DbObjectRef("SAMPLE", "ORDERS", "TABLE");

        Assert.Equal(new ExternalRevealResult(false, false), await revealer.RevealAsync(target, new ReorderSession(), open: false, CancellationToken.None));
        Assert.Empty(opened);

        Assert.Equal(new ExternalRevealResult(false, true), await revealer.RevealAsync(target, new ReorderSession(), open: true, CancellationToken.None));
        Assert.Equal(target, Assert.Single(opened));
    }
}
