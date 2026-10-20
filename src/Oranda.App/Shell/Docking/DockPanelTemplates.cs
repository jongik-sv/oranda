using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// <see cref="PanelTool"/>·<see cref="PanelDocument"/> 를 그리는 템플릿(내용 = <c>Panel</c> ViewModel, View 는 ViewLocator 가 찾는다).
/// 떠 있는 창(Dock HostWindow)은 <see cref="DockHostView"/> 와 다른 시각 트리 뿌리라 그 안에 둔 템플릿을 상속하지 못하므로,
/// 앱 수준 템플릿 목록에 한 번만 넣어 메인 창과 떠 있는 창이 함께 쓴다.
/// </summary>
internal static class DockPanelTemplates
{
    private static readonly IDataTemplate[] Templates =
    [
        new FuncDataTemplate<PanelTool>((tool, _) => tool is null ? null : Present(tool.Panel)),
        new FuncDataTemplate<PanelDocument>((document, _) => document is null ? null : Present(document.Panel)),
    ];

    /// <summary>앱 수준 템플릿 목록에 넣는다(여러 번 불러도 한 번만).</summary>
    public static void Register()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (var template in Templates)
        {
            if (!app.DataTemplates.Contains(template))
            {
                app.DataTemplates.Insert(0, template);
            }
        }
    }

    private static ContentControl Present(object panel) => new() { Content = panel };
}
