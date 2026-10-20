using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <c>oranda.panel.binds</c> — 바인드 변수 창(EXEC-03, 오렌지 Bind Variable List). 편집기 오른쪽에 붙어 바인드 변수의
/// 값·형식·방향을 둔다. 실행은 묻지 않고 이 값을 쓴다. 처음 보는 변수는 실행할 때 여기에 줄이 생긴다. 직접 더할 수도 있다.
/// 탭의 SQL 이 참조하는 이름의 값은 그 탭과 함께 작업 공간(<c>workspaces/{id}.json</c>·<c>autosave/{id}.json</c>)과
/// .sql 파일 끝 바인드 블록(<c>-- @oranda-binds v1</c>)에 저장되고, 탭이나 파일을 다시 열면 이 창에 그 값이 들어간다.
/// 이름이 비밀로 보이는 바인드(pass·pwd·secret·token)는 저장하지 않는다.
/// </summary>
[OrandaToolPanel("oranda.panel.binds", ToolPanelKind.Tool, DockLocation.Right, Order = 20, DefaultSize = 380)]
public sealed partial class BindVariablesViewModel : ToolPanelViewModelBase
{
    // 이벤트 버스만 받는다(등록된 서비스라 지연 어셈블리를 깨우지 않는다). 생성자는 이것 하나만 둔다.
    public BindVariablesViewModel(IEventBus? events = null)
    {
        Store = BindVariableStore.For(events);
    }

    public BindVariableStore Store { get; }

    public ObservableCollection<BindVariableRow> Rows => Store.Rows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private BindVariableRow? _selected;

    /// <summary>빈 줄을 더하고 고른다(이름은 사용자가 넣는다).</summary>
    [RelayCommand]
    private void Add()
    {
        var row = Store.Add(string.Empty);
        Selected = row;
        Store.RequestFocus(string.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is { } row)
        {
            var index = Rows.IndexOf(row);
            Store.Remove(row);
            Selected = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        }
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand]
    private void Clear()
    {
        Store.Clear();
        Selected = null;
    }

    /// <summary>편집기의 바인드 변수를 값으로 바꿔 넣는다(오렌지 「Paste Bind Variables Into Editor」).</summary>
    [RelayCommand]
    private Task PasteIntoEditor() => Rows.Count == 0 ? Task.CompletedTask : ShellCommandRunner.RunAsync("oranda.exec.pasteBindValues");
}
