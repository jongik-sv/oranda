using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ExtensionTest;

/// <summary><c>oranda.panel.extensionTest</c> — TSK-10-03 빈 틀. 실행 로직 없음.</summary>
[OrandaToolPanel("oranda.panel.extensionTest", ToolPanelKind.Tool, DockLocation.Right, Order = 40, DevBuildOnly = true)]
public sealed class ExtensionTestViewModel : ToolPanelViewModelBase
{
}
