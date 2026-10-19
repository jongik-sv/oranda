using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary><c>oranda.panel.messages</c> — 실행 로그·스크립트 오류 메시지 창. 줄은 <see cref="ExecutionLog"/> 에서 온다.</summary>
[OrandaToolPanel("oranda.panel.messages", ToolPanelKind.Tool, DockLocation.Bottom, Order = 20)]
public sealed partial class MessagesViewModel : ToolPanelViewModelBase
{
    // 생성자는 이것 하나만 둔다(OutputViewModel 참고).
    public MessagesViewModel(IEventBus? events = null)
    {
        Log = ExecutionLog.For(events);
    }

    public ExecutionLog Log { get; }

    public ObservableCollection<MessageEntry> Messages => Log.Messages;

    [RelayCommand]
    private void Clear() => Log.ClearMessages();
}
