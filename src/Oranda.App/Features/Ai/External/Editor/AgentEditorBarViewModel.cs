using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>띠 항목 종류.</summary>
public enum AgentBarItemKind
{
    /// <summary>편집 제안: 「비교 보기」·「받아들이기」·「버리기」.</summary>
    Proposal,

    /// <summary>저장 요청: 「저장」·「무시」.</summary>
    Save,

    /// <summary>알림(새 탭의 변경문 안내 등): 「닫기」.</summary>
    Notice,
}

/// <summary>
/// 띠에 줄 서는 항목 하나. 단추가 하는 일은 만든 쪽(<see cref="ExternalEditorService"/>)이 넘긴다.
/// </summary>
public sealed class AgentBarItem(AgentBarItemKind kind, string headline)
{
    public AgentBarItemKind Kind { get; } = kind;

    /// <summary>머리 글(「Claude Code 가 편집을 제안했습니다」).</summary>
    public string Headline { get; } = headline;

    /// <summary>제목·까닭 한 줄. 없으면 빈 글.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>「비교 보기」(보기만). 제안 항목만.</summary>
    public Func<Task>? Compare { get; init; }

    /// <summary>「받아들이기」·「저장」. 끝났으면 null, 항목을 남기고 알릴 글이 있으면 그 글.</summary>
    public Func<Task<string?>>? Accept { get; init; }

    /// <summary>「버리기」·「무시」·「닫기」.</summary>
    public Action? Discard { get; init; }
}

/// <summary>
/// 편집기 아래 밖의 에이전트 띠(설계 agent-cli-control §6.1). 탭마다 하나이고 항목을 줄 세워 맨 앞 하나만 보인다(「대기 N」).
/// 모달 창을 먼저 띄우지 않는다: 비교 창은 사람이 단추를 눌렀을 때만 열린다. UI 스레드에서 쓴다.
/// </summary>
public sealed partial class AgentEditorBarViewModel : ObservableObject
{
    private readonly List<AgentBarItem> _items = [];
    private bool _busy;

    /// <summary>보이는 항목(맨 앞). 없으면 null.</summary>
    public AgentBarItem? Current => _items.Count > 0 ? _items[0] : null;

    /// <summary>줄 선 항목 전부(맨 앞 포함).</summary>
    public IReadOnlyList<AgentBarItem> Items => _items;

    public bool IsVisible => Current is not null;

    public string Headline => Current?.Headline ?? string.Empty;

    public string Detail => Current?.Detail ?? string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>뒤에 줄 선 항목 수 글(「대기 1」). 없으면 빈 글.</summary>
    public string QueueText => _items.Count > 1 ? ExternalEditorText.Queue(_items.Count - 1) : string.Empty;

    public bool HasQueue => _items.Count > 1;

    public bool ShowsCompare => Current is { Kind: AgentBarItemKind.Proposal, Compare: not null };

    public bool ShowsAccept => Current is { Accept: not null };

    public string AcceptLabel => Current?.Kind == AgentBarItemKind.Save ? ExternalEditorText.Save : ExternalEditorText.Accept;

    public string DiscardLabel => Current?.Kind switch
    {
        AgentBarItemKind.Save => ExternalEditorText.Ignore,
        AgentBarItemKind.Notice => ExternalEditorText.Close,
        _ => ExternalEditorText.Discard,
    };

    /// <summary>상태 한 줄(넣지 못한 까닭 등).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>항목을 줄 끝에 세운다.</summary>
    public void Enqueue(AgentBarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var wasCurrent = Current;
        _items.Add(item);
        Changed(!ReferenceEquals(wasCurrent, Current));
    }

    /// <summary>항목을 뺀다(끝났거나 어긋났거나 탭이 닫혔을 때). 없으면 아무 일도 없다.</summary>
    public bool Remove(AgentBarItem item)
    {
        var wasCurrent = Current;
        if (!_items.Remove(item))
        {
            return false;
        }

        Changed(!ReferenceEquals(wasCurrent, Current));
        return true;
    }

    private void Changed(bool headChanged)
    {
        if (headChanged)
        {
            StatusText = string.Empty;
        }

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(ShowsCompare));
        OnPropertyChanged(nameof(ShowsAccept));
        OnPropertyChanged(nameof(AcceptLabel));
        OnPropertyChanged(nameof(DiscardLabel));
        CompareCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private bool CanCompare() => ShowsCompare && !_busy;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync() => RunBusyAsync(Current?.Compare is { } compare ? async () => { await compare().ConfigureAwait(true); return null; } : null);

    private bool CanAccept() => ShowsAccept && !_busy;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private Task AcceptAsync() => RunBusyAsync(Current?.Accept);

    private bool CanDiscard() => IsVisible && !_busy;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        if (Current is not { } item)
        {
            return;
        }

        if (item.Discard is { } discard)
        {
            discard();
        }

        Remove(item); // 만든 쪽이 이미 뺐으면 아무 일도 없다
    }

    private async Task RunBusyAsync(Func<Task<string?>>? action)
    {
        if (action is null)
        {
            return;
        }

        var item = Current;
        SetBusy(true);
        try
        {
            var status = await action().ConfigureAwait(true);
            if (status is not null && ReferenceEquals(item, Current))
            {
                StatusText = status;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CompareCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}
