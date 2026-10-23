using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;

/// <summary>
/// 문장 편집기 강조 꾸밈(Order 0, design.md 9장). 편집기 View 가 SQL 강조를 떼고 나면 이 꾸밈이 xml·MyBatis 표지를 아는 변환기를 붙인다.
/// 돌려주는 값을 Dispose 하면 변환기가 빠진다.
/// </summary>
[OrandaService(typeof(IMyBatisEditorDecorator))]
public sealed class MyBatisHighlightDecorator : IMyBatisEditorDecorator
{
    public int Order => 0;

    public IDisposable Attach(MyBatisEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MyBatisSyntaxColorizer(context.Editor);
    }
}
