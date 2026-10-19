using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary><c>oranda.panel.output</c> — DBMS_OUTPUT 창(EXEC-04). 글은 <see cref="ExecutionLog"/> 에서 온다.</summary>
[OrandaToolPanel("oranda.panel.output", ToolPanelKind.Tool, DockLocation.Bottom, Order = 10)]
public sealed partial class OutputViewModel : ToolPanelViewModelBase
{
    // 이벤트 버스만 받는다(등록된 서비스라 지연 어셈블리를 깨우지 않는다). 없으면 공용 기록.
    // 생성자는 이것 하나만 둔다: 다른 생성자가 등록 안 된 형식을 받으면 컨테이너가 지연 어셈블리를 훑는다(시작 시험이 막는다).
    public OutputViewModel(IEventBus? events = null)
    {
        Log = ExecutionLog.For(events);
        State = ExecutionState.For(events);
        var log = Log;
        var state = State;
        log.OutputChanged += (_, _) => OnPropertyChanged(nameof(Text));
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExecutionState.DbmsOutputEnabled))
            {
                OnPropertyChanged(nameof(Header));
            }
        };
    }

    public ExecutionLog Log { get; }

    public ExecutionState State { get; }

    public string Text => Log.OutputText;

    public string Header => State.DbmsOutputEnabled ? "DBMS_OUTPUT" : "DBMS_OUTPUT (수집 꺼짐)";

    [RelayCommand]
    private void Clear() => Log.ClearOutput();
}
