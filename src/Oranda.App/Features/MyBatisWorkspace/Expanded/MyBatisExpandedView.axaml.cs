using Avalonia.Controls;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

public partial class MyBatisExpandedView : UserControl
{
    public MyBatisExpandedView()
    {
        InitializeComponent();

        // 우클릭 메뉴는 셸이 명령 레지스트리의 context:mybatisExpanded 배치로 열 때마다 만든다(design.md 4.1).
        ShellContextMenus.Attach(SqlView.TextEditorControl, "mybatisExpanded");
        AttachedToVisualTree += (_, _) => BuildActionBar();
        DataContextChanged += (_, _) => BuildActionBar();
    }

    /// <summary>머리 줄 오른쪽 아이콘 단추. 주 도구 막대와 같은 모양(<c>toolbar</c> 클래스)이며 실행은 레지스트리 명령 한 경로다.</summary>
    private void BuildActionBar()
    {
        if (DataContext is not MyBatisExpandedViewModel vm)
        {
            return;
        }

        vm.Attach();
        ActionBar.Children.Clear();
        foreach (var action in vm.Actions)
        {
            var icon = ShellIcons.Create(action.Icon);
            var button = new Button
            {
                Content = icon ?? (object)action.Title,
                Command = action.Command,
                Classes = { "toolbar", icon is null ? "toolbar-text" : "toolbar-icon" },
            };
            Avalonia.Automation.AutomationProperties.SetName(button, action.Title);
            ToolTip.SetTip(button, action.Title);
            ActionBar.Children.Add(button);
        }
    }
}
