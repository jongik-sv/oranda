using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Binds;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <c>oranda.panel.binds</c> — 바인드 변수 창(EXEC-03, 오렌지 Bind Variable List). 편집기 오른쪽에 붙어 바인드 변수의
/// 값·형식·방향을 둔다. 실행은 묻지 않고 이 값을 쓴다. 처음 보는 변수는 실행할 때 여기에 줄이 생긴다. 직접 더할 수도 있다.
/// 탭의 SQL 이 참조하는 이름의 값은 그 탭과 함께 작업 공간(<c>workspaces/{id}.json</c>·<c>autosave/{id}.json</c>)과
/// .sql 파일 끝 바인드 블록(<c>-- @oranda-binds v1</c>)에 저장되고, 탭이나 파일을 다시 열면 이 창에 그 값이 들어간다.
/// 값이 없는 바인드는 NULL 로, 이름이 비밀로 보이는 바인드(pass·pwd·secret·token)도 값 그대로 저장한다.
/// </summary>
[OrandaToolPanel("oranda.panel.binds", ToolPanelKind.Tool, DockLocation.Right, Order = 20, DefaultSize = 380)]
public sealed partial class BindVariablesViewModel : ToolPanelViewModelBase
{
    private readonly IEventBus? _events;
    private readonly SqlEditorHost _host;

    // 이벤트 버스만 받는다(등록된 서비스라 지연 어셈블리를 깨우지 않는다). 생성자는 이것 하나만 둔다.
    public BindVariablesViewModel(IEventBus? events = null)
        : this(events, static () => SqlEditorHost.Current)
    {
    }

    // SqlEditorHost 를 생성자 인자로 두면 서비스 컨테이너가 인자를 풀려고 지연 어셈블리를 깨운다(기본 배치에서 이 창이 시작 때 만들어진다).
    // 그래서 호스트는 Func 로 받고, 컨테이너가 고르지 못하는 비공개 생성자에 둔다.
    private BindVariablesViewModel(IEventBus? events, Func<SqlEditorHost> hostFactory)
    {
        _events = events;
        _host = hostFactory();
        _store = BindVariableStore.ForTab(_host.Active, _events);
        _host.ActiveChanged += (_, _) => FollowActiveTab();
    }

    /// <summary>활성 SQL 탭을 주는 호스트를 직접 받는다(시험용).</summary>
    internal static BindVariablesViewModel ForHost(IEventBus? events, SqlEditorHost host) => new(events, () => host);

    /// <summary>지금 보이는 목록: 활성 SQL 탭의 것(TSK-04-11). 탭이 없으면 도구 창용 기본 목록.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private BindVariableStore _store;

    public ObservableCollection<BindVariableRow> Rows => Store.Rows;

    private void FollowActiveTab()
    {
        var next = BindVariableStore.ForTab(_host.Active, _events);
        if (!ReferenceEquals(next, Store))
        {
            Selected = null;
            Store = next;
        }
    }

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

    /// <summary>블록으로 고른 줄(클릭·Shift·Ctrl). 뷰가 채운다. 비어 있으면 <see cref="Selected"/> 하나.</summary>
    public IList<BindVariableRow> SelectedRows { get; set; } = [];

    /// <summary>창 아래 줄 글. 복사·붙여 넣기 결과를 알린다.</summary>
    [ObservableProperty]
    private string _statusText = DefaultStatus;

    private const string DefaultStatus = "실행하면 문장의 :변수가 여기에 채워집니다. 빈 값은 NULL.";

    private List<BindVariableRow> Block()
    {
        var rows = SelectedRows.Where(Rows.Contains).OrderBy(Rows.IndexOf).ToList();
        if (rows.Count == 0 && Selected is { } one)
        {
            rows.Add(one);
        }

        return rows;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var block = Block();
        if (block.Count == 0)
        {
            return;
        }

        var index = Rows.IndexOf(block[0]);
        foreach (var row in block)
        {
            Store.Remove(row);
        }

        Selected = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
    }

    /// <summary>고른 줄(없거나 <paramref name="all"/> 이면 전체)을 TSV 4열로. 복사할 줄이 없으면 null.</summary>
    public string? CopyText(bool withHeader = false, bool all = false)
    {
        var block = all ? [] : Block();
        if (block.Count == 0)
        {
            block = [.. Rows];
        }

        if (block.Count == 0)
        {
            return null;
        }

        StatusText = $"바인드 변수 {block.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}줄을 복사했습니다.";
        return BindTsv.Format(block, withHeader);
    }

    /// <summary>
    /// TSV 를 읽어 이름이 같은 변수에 값·자료형·방향을 채운다. 활성 탭의 SQL 이 쓰는 이름은 아직 줄이 없어도(실행 전) 줄을 만든다.
    /// 채운 줄 수를 돌려준다.
    /// </summary>
    public int PasteText(string text)
    {
        var tab = _host.Active;
        var addable = tab is not null && ReferenceEquals(tab.Binds, Store) ? tab.ReferencedBindNames : null;
        var (applied, skipped) = BindTsv.Apply(Store, text, addable: addable);
        StatusText = skipped > 0
            ? $"바인드 변수 {applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}줄에 붙여 넣었습니다(목록에 없는 이름 {skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)}개는 건너뜀)."
            : $"바인드 변수 {applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}줄에 붙여 넣었습니다.";
        return applied;
    }

    private bool CanDelete() => Selected is not null || SelectedRows.Count > 0;

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
