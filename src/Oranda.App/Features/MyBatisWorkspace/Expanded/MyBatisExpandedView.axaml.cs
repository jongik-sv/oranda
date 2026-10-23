using Avalonia;
using Avalonia.Controls;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

/// <summary>
/// 「펼친 SQL」 분할 영역(TSK-03-10): MyBatis 문장 탭 안에서 원문 편집기 옆·아래에 붙는다(<see cref="Editor.MyBatisStatementEditorView"/>).
/// 화면에 붙을 때 ViewModel 을 허브에 붙이고(<see cref="MyBatisExpandedViewModel.Attach"/>), 떨어질 때 뗀다(탭을 닫거나 다른 탭으로 가면 구독을 남기지 않는다).
/// </summary>
public partial class MyBatisExpandedView : UserControl
{
    /// <summary>우클릭 메뉴 표면 이름(레지스트리 <c>context:mybatisExpanded/…</c>).</summary>
    internal const string ContextSurface = "mybatisExpanded";

    /// <summary>분할 두 칸 머리 줄의 높이(DIP): 원문 칸의 문장 머리 줄과 이 영역의 시나리오 바가 같은 높이에서 시작하고 끝난다.</summary>
    public const double PaneHeaderHeight = 30;

    private MyBatisExpandedViewModel? _attached;

    public MyBatisExpandedView()
    {
        InitializeComponent();

        // 우클릭 메뉴는 셸이 명령 레지스트리의 context:mybatisExpanded 배치로 열 때마다 만든다(design.md 4.1).
        ShellContextMenus.Attach(SqlView.TextEditorControl, ContextSurface);
        DataContextChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not null)
            {
                BuildActionBar();
            }
        };
    }

    /// <summary>읽기 전용 펼친 SQL 편집기(시험·계측용).</summary>
    internal Oranda.App.Features.SqlEditor.SqlCodeView CodeView => SqlView;

    /// <summary>시나리오 바(분할 머리 줄, 시험·계측용).</summary>
    internal Border PaneHeader => ScenarioBar;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BuildActionBar();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached?.Detach();
        _attached = null;
        ActionBar.Children.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>머리 줄 오른쪽 아이콘 단추. 주 도구 막대와 같은 모양(<c>toolbar</c> 클래스)이며 실행은 레지스트리 명령 한 경로다.</summary>
    private void BuildActionBar()
    {
        ActionBar.Children.Clear();
        var vm = DataContext as MyBatisExpandedViewModel;
        if (!ReferenceEquals(vm, _attached))
        {
            _attached?.Detach(); // 다른 ViewModel 로 바뀌면 앞의 것은 구독을 끊는다
            _attached = vm;
        }

        if (vm is null)
        {
            return;
        }

        vm.Attach();
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
