using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속 목록 정렬(이름 또는 최근 접속이 먼저).</summary>
public enum ConnectionSortOrder
{
    Name,
    RecentlyConnected,
}

/// <summary>
/// 접속 관리자 대화상자의 모델(Ctrl+Shift+O): 접속 트리, 프로필 편집, 접속, 지갑 가져오기(CONN-01~04, 06, 08).
/// 비밀번호 입력·확인 같은 화면 작업은 <see cref="AskPassword"/>·<see cref="AskChangePassword"/>·<see cref="Confirm"/> 로 화면이 맡는다.
/// </summary>
public sealed partial class ConnectionManagerViewModel : ViewModelBase
{
    private readonly IConnectionService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasAnySelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ConnectCommand), nameof(DuplicateCommand), nameof(CopyConnectionStringCommand), nameof(RenameFolderCommand), nameof(MoveToFolderCommand))]
    private ConnectionTreeNode? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(ConnectCommand), nameof(SaveCommand))]
    private ProfileEditorViewModel? _editor;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsError))]
    private StatusKind _statusKind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(TestCommand), nameof(SaveCommand), nameof(DeleteCommand), nameof(DuplicateCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _fileNotice;

    /// <summary>목록 찾기 글자(이름·사용자·그룹·접속 문자열에서 대소문자 무시 부분 일치).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ConnectionSortOrder _sortOrder = ConnectionSortOrder.Name;

    /// <summary>마지막으로 읽은 프로필 전체(찾기·정렬은 이 목록으로 트리를 다시 만든다).</summary>
    private List<ConnectionProfile> _profiles = [];

    /// <summary>마지막으로 읽은 폴더 경로 전체(빈 폴더와 접속이 가리키는 폴더, 상위 폴더 포함).</summary>
    private IReadOnlyList<string> _allFolders = [];

    private Func<Task<bool>>? _importConnections;

    private Func<Task<bool>>? _exportConnections;

    /// <summary>사용자가 접은 그룹(창이 열려 있는 동안만 기억한다).</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>트리를 다시 만드는 중(이때 생기는 선택 변화는 편집기를 바꾸지 않는다).</summary>
    private bool _rebuilding;

    public ConnectionManagerViewModel(IConnectionService service)
    {
        _service = service;
    }

    public ObservableCollection<ConnectionTreeNode> Tree { get; } = [];

    /// <summary>정렬 선택지(화면 콤보).</summary>
    public IReadOnlyList<ChoiceItem<ConnectionSortOrder>> SortOrders { get; } =
    [
        new(ConnectionSortOrder.Name, "이름순"),
        new(ConnectionSortOrder.RecentlyConnected, "최근 접속순"),
    ];

    /// <summary>접속 성공 뒤 대화상자를 닫으라는 신호.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>비밀번호를 묻는다(접속 이름 → 입력값, 취소면 null).</summary>
    public Func<ConnectionProfile, Task<string?>>? AskPassword { get; set; }

    /// <summary>비밀번호 변경 대화상자(접속 → (현재, 새) 비밀번호, 취소면 null). 현재 비밀번호를 비워 두면 저장된 값을 쓴다.</summary>
    public Func<ConnectionProfile, string?, Task<(string? Old, string New)?>>? AskChangePassword { get; set; }

    /// <summary>삭제 같은 되돌릴 수 없는 작업 확인(메시지 → 예/아니오). 지정하지 않으면 예.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// 「폴더로 이동…」 에서 폴더를 고른다(고를 수 있는 폴더 경로 목록, 지금 폴더 → 고른 결과, 취소하면 null).
    /// 지정하지 않으면 이동하지 않는다.
    /// </summary>
    public Func<IReadOnlyList<string>, string?, Task<FolderChoice?>>? AskFolder { get; set; }

    /// <summary>폴더 이름 편집을 시작했다(화면이 입력칸에 초점을 준다).</summary>
    public event EventHandler<ConnectionTreeNode>? FolderEditRequested;

    /// <summary>
    /// 접속 가져오기(「파일 &gt; 접속 가져오기」 와 같은 흐름: 파일 고르기 → 저장 → 결과 알림). 창을 띄운 쪽이 채운다.
    /// 하나라도 가져왔으면 true 를 돌려주고, 그러면 목록을 다시 읽는다.
    /// </summary>
    public Func<Task<bool>>? ImportConnections
    {
        get => _importConnections;
        set
        {
            _importConnections = value;
            ImportCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>접속 내보내기(「파일 &gt; 접속 내보내기」 와 같은 흐름: 체크 목록 → 저장 대화상자 → 알림, TSK-13-13). 창을 띄운 쪽이 채운다.</summary>
    public Func<Task<bool>>? ExportConnections
    {
        get => _exportConnections;
        set
        {
            _exportConnections = value;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 저장하지 않은 편집이 있을 때 다른 접속을 고르거나 창을 닫으려 하면 묻는다(메시지 → 저장·버리기·취소).
    /// 지정하지 않으면 버린다(이전 동작).
    /// </summary>
    public Func<string, Task<UnsavedChoice>>? AskUnsaved { get; set; }

    /// <summary>저장 확인 때문에 미뤄 둔 접속 전환(시험이 끝나기를 기다릴 때 쓴다). 없으면 끝난 작업.</summary>
    public Task PendingSelection { get; private set; } = Task.CompletedTask;

    /// <summary>글자를 클립보드에 넣는다(화면이 채운다). 연결 문자열 복사에 쓰며 비밀번호는 넘기지 않는다.</summary>
    public Func<string, Task>? CopyText { get; set; }

    public bool HasSelection => Selected?.Profile is not null;

    /// <summary>접속 또는 폴더가 골라져 있다(삭제·이동 대상).</summary>
    public bool HasAnySelection => Selected is not null;

    /// <summary>폴더 경로 전체(편집기 그룹 칸과 「폴더로 이동」 목록이 쓴다).</summary>
    public IReadOnlyList<string> AllFolders => _allFolders;

    public bool StatusIsError => StatusKind == StatusKind.Error;

    /// <summary>접속에 성공한 마지막 결과(시험·호출자용).</summary>
    public ConnectOutcome? LastOutcome { get; private set; }

    public void ReportError(string text) => SetStatus(StatusKind.Error, text);

    private void SetStatus(StatusKind kind, string text)
    {
        StatusKind = kind;
        Status = text;
    }
}

/// <summary>「폴더로 이동」 에서 고른 폴더. <see cref="Path"/> 가 null 이면 「(폴더 없음)」.</summary>
public sealed record FolderChoice(string? Path);

/// <summary>저장하지 않은 편집을 어떻게 할지(저장 확인 대화상자의 답).</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>상태 줄 종류.</summary>
public enum StatusKind
{
    Info,
    Error,
}
