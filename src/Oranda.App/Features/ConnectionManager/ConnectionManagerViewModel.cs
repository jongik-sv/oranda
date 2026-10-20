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

/// <summary>접속 트리의 한 줄: 폴더(그룹) 또는 접속.</summary>
public sealed partial class ConnectionTreeNode : ObservableObject
{
    /// <summary>폴더면 접혀 있는지, 접속이면 편집 중인 값이 저장되지 않았는지를 화면이 그린다.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isModified;

    /// <summary>폴더 이름을 고치는 중(트리 줄이 글자 입력칸으로 바뀐다).</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>폴더 이름 입력칸의 글자.</summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    /// <summary>끌어 놓을 대상으로 강조 중인 폴더 줄.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public ConnectionTreeNode(string title, ConnectionProfile? profile = null, string? path = null)
    {
        Title = title;
        Profile = profile;
        Path = path ?? string.Empty;
    }

    /// <summary>폴더면 이름(경로의 마지막 단계), 접속이면 접속 이름.</summary>
    public string Title { get; }

    /// <summary>폴더면 전체 경로(「부모/자식」), 접속이면 빈 글자.</summary>
    public string Path { get; }

    /// <summary>폴더면 하위 폴더까지 포함한 접속 수(트리를 채운 뒤 정한다).</summary>
    public int ProfileCount { get; set; }

    /// <summary>접속이면 그 프로필, 폴더면 null.</summary>
    public ConnectionProfile? Profile { get; }

    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    public bool IsFolder => Profile is null;

    /// <summary>환경 칩 글자(색만으로 구분하지 않는다, ui-shell.md 4.1). 폴더면 빈 글자.</summary>
    public string EnvironmentLabel => Profile?.Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Development => "개발",
        _ => string.Empty,
    };

    /// <summary>환경 칩 바탕색(<c>#RRGGBB</c>). 운영은 항상 빨강, 나머지는 사용자 색 또는 환경 기본색.</summary>
    public string EnvironmentColor => Profile switch
    {
        null => "#00000000",
        { Environment: ConnectionEnvironment.Production } => "#C42B1C",
        { Color: { Length: > 0 } c } when LabelColorRules.TryParse(c, out _, out _, out _) => c,
        { Environment: ConnectionEnvironment.Test } => "#B65A00",
        _ => "#107C10",
    };

    public bool HasChip => Profile is not null;

    /// <summary>화면 낭독기용 이름(운영은 「운영 접속」).</summary>
    public string AccessibleName => Profile is null
        ? Title
        : Profile.Environment == ConnectionEnvironment.Production ? $"{Title}, 운영 접속" : $"{Title}, {EnvironmentLabel}";

    public string Subtitle => Profile is null ? $"({ProfileCount})" : $"{Profile.User}@{Profile.DataSource}";

    /// <summary>마지막 접속 시각(짧게). 접속한 적이 없거나 폴더면 빈 글자.</summary>
    public string LastConnectedText => Profile?.LastConnectedAt is { } at ? FormatWhen(at, DateTimeOffset.Now) : string.Empty;

    /// <summary>마지막 접속 시각 전체(풍선 도움말).</summary>
    public string? LastConnectedTip => Profile?.LastConnectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>오늘이면 <c>HH:mm</c>, 올해면 <c>MM-dd</c>, 그 전이면 <c>yyyy-MM-dd</c>(목록 폭이 좁아서 짧게 쓴다).</summary>
    public static string FormatWhen(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToOffset(now.Offset);
        var format = local.Date == now.Date ? "HH:mm" : local.Year == now.Year ? "MM-dd" : "yyyy-MM-dd";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }
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

    /// <summary>사용자가 접은 그룹(창이 열려 있는 동안만 기억한다).</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);

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

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var doc = await _service.LoadAsync(ct).ConfigureAwait(true);
        FileNotice = doc.Notice;
        Rebuild(doc.Value, null);
    }

    [RelayCommand]
    private async Task AddNewAsync()
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        SelectQuietly(null);
        Editor = ProfileEditorViewModel.ForNew();
        SetStatus(StatusKind.Info, "새 접속을 입력하세요.");
    }

    /// <summary>
    /// 저장하지 않은 편집이 있으면 저장·버리기·취소를 묻는다. 진행해도 되면 true(저장 성공 또는 버림), 취소하거나 저장에 실패하면 false.
    /// 창을 닫기 전, 새 접속·복제 전, 다른 접속을 고를 때 쓴다.
    /// </summary>
    public async Task<bool> ResolveUnsavedAsync()
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        var name = editor.IsNew ? (string.IsNullOrWhiteSpace(editor.Name) ? "새 접속" : editor.Name.Trim()) : editor.Original.Name;
        var choice = AskUnsaved is null
            ? UnsavedChoice.Discard
            : await AskUnsaved($"'{name}' 의 바뀐 내용을 저장할까요?").ConfigureAwait(true);
        switch (choice)
        {
            case UnsavedChoice.Save:
                return await SaveCurrentAsync().ConfigureAwait(true);
            case UnsavedChoice.Discard:
                Editor = editor.IsNew ? null : ProfileEditorViewModel.From(editor.Original);
                return true;
            default:
                return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is { IsFolder: true } folder)
        {
            await DeleteFolderAsync(folder).ConfigureAwait(true);
            return;
        }

        if (Selected?.Profile is not { } profile)
        {
            return;
        }

        var ok = Confirm is null || await Confirm($"'{profile.Name}' 접속을 지울까요? 저장된 비밀번호도 함께 지웁니다.").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _service.DeleteProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (ConnectionValidationException ex)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return;
        }

        Editor = null;
        await LoadAsync().ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"'{profile.Name}' 접속을 지웠습니다.");
    }

    /// <summary>고른 접속을 복제한다(이름 뒤 「 (사본)」, 비밀번호는 복사하지 않는다). 사본을 골라 둔다.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private async Task DuplicateAsync()
    {
        if (Selected?.Profile is not { } profile || !await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        ConnectionProfile copy;
        try
        {
            copy = await _service.DuplicateProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ConnectionValidationException or System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return;
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        Rebuild(doc.Value, copy.Id);
        SetStatus(StatusKind.Info, $"'{profile.Name}' 을(를) '{copy.Name}' (으)로 복제했습니다. 비밀번호는 복사하지 않았습니다.");
    }

    /// <summary>접속 가져오기 흐름을 돌리고, 가져왔으면 목록을 다시 읽는다(편집 중인 값은 그대로 둔다).</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (ImportConnections is null || !await ImportConnections().ConfigureAwait(true))
        {
            return;
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        FileNotice = doc.Notice;
        AdoptDocument(doc.Value);
        ApplyView(null, reloadEditor: false);
    }

    private bool CanImport() => ImportConnections is not null;

    /// <summary>고른 접속의 저장된 연결 문자열을 클립보드에 넣는다(비밀번호 없음).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyConnectionStringAsync()
    {
        if (Selected?.Profile is { } profile)
        {
            await CopyToClipboardAsync(DataSourceComposer.Describe(profile)).ConfigureAwait(true);
        }
    }

    /// <summary>폼 아래 연결 문자열 미리보기를 클립보드에 넣는다(비밀번호 없음).</summary>
    [RelayCommand]
    private Task CopyPreviewAsync() => CopyToClipboardAsync(Editor?.ConnectionStringPreview ?? string.Empty);

    private async Task CopyToClipboardAsync(string text)
    {
        if (text.Length == 0)
        {
            SetStatus(StatusKind.Error, "복사할 연결 문자열이 없습니다.");
            return;
        }

        if (CopyText is not null)
        {
            await CopyText(text).ConfigureAwait(true);
        }

        SetStatus(StatusKind.Info, "연결 문자열을 복사했습니다(비밀번호는 넣지 않습니다).");
    }

    /// <summary>
    /// 접속 시험: 지금 폼 값(저장 안 한 값 포함)으로 임시 연결을 열어 서버 버전·걸린 시간을 상태 줄에 보이고 바로 닫는다.
    /// 비밀번호 칸이 비면 저장된 값을, 그것도 없으면 비밀번호 대화상자를 쓴다. 비밀번호는 저장하지 않는다.
    /// 시험하는 동안 이 명령은 꺼지고 <c>TestCancelCommand</c> 로 멈출 수 있다. 시간 제한은 폼의 「접속 제한(초)」.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTest))]
    private async Task TestAsync(CancellationToken cancel)
    {
        if (Editor is null)
        {
            return;
        }

        var profile = Editor.TryBuild(out var errors);
        if (profile is null)
        {
            SetStatus(StatusKind.Error, string.Join(Environment.NewLine, errors));
            return;
        }

        var typed = string.IsNullOrEmpty(Editor.Password) ? null : Editor.Password;
        var limit = TimeSpan.FromSeconds(profile.ConnectTimeoutSeconds);
        IsBusy = true;
        try
        {
            while (true)
            {
                SetStatus(StatusKind.Info, $"'{profile.Name}' 접속을 시험하는 중입니다…");
                ConnectionTestOutcome outcome;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    timeout.CancelAfter(limit);
                    try
                    {
                        outcome = await _service.TestConnectionAsync(profile, typed, timeout.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        SetStatus(
                            cancel.IsCancellationRequested ? StatusKind.Info : StatusKind.Error,
                            cancel.IsCancellationRequested ? "접속 시험을 취소했습니다." : $"접속 시험이 제한 시간({limit.TotalSeconds:0}초)을 넘겨 멈췄습니다.");
                        return;
                    }
                }

                if (outcome.Status != ConnectStatus.PasswordRequired)
                {
                    LastTestOutcome = outcome;
                    SetStatus(outcome.Succeeded ? StatusKind.Info : StatusKind.Error, outcome.Message);
                    return;
                }

                if (AskPassword is null || await AskPassword(profile).ConfigureAwait(true) is not { } pw)
                {
                    SetStatus(StatusKind.Info, "접속 시험을 취소했습니다.");
                    return;
                }

                typed = pw;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTest() => Editor is { HasRequiredFields: true } && !IsBusy;

    /// <summary>고른 접속을 지우거나 복제할 수 있는지(시험·접속 중에는 끈다).</summary>
    private bool CanChangeSelection() => HasSelection && !IsBusy;

    private bool CanDelete() => HasAnySelection && !IsBusy;

    private bool CanConnect() => Editor is { HasRequiredFields: true } && !IsBusy;

    /// <summary>마지막 접속 시험 결과(시험용).</summary>
    public ConnectionTestOutcome? LastTestOutcome { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveCurrentAsync().ConfigureAwait(true);

    private bool CanSave() => Editor is { IsDirty: true } && !IsBusy;

    /// <summary>편집 중인 내용을 저장한다. 성공하면 true.</summary>
    public async Task<bool> SaveCurrentAsync() => await SaveAndGetAsync().ConfigureAwait(true) is not null;

    /// <summary>편집 중인 내용을 저장하고 저장된 프로필을 돌려준다. 검증에 걸리거나 실패하면 null(상태 줄에 이유).</summary>
    private async Task<ConnectionProfile?> SaveAndGetAsync()
    {
        if (Editor is null)
        {
            return null;
        }

        var profile = Editor.TryBuild(out var errors);
        if (profile is null)
        {
            SetStatus(StatusKind.Error, string.Join(Environment.NewLine, errors));
            return null;
        }

        try
        {
            var saved = await _service.SaveProfileAsync(profile, Editor.Password, Editor.SavePassword, CancellationToken.None).ConfigureAwait(true);
            Editor.Password = string.Empty;
            var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild(doc.Value, saved.Id);
            SetStatus(StatusKind.Info, $"'{saved.Name}' 을(를) 저장했습니다.");
            return saved;
        }
        catch (ConnectionValidationException ex)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return null;
        }
    }

    /// <summary>편집 내용을 저장하고(필요하면) 접속한다. 성공하면 <see cref="CloseRequested"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 저장 안 한 값이 있으면 먼저 저장한다. 비밀번호 칸의 값은 저장하지 않는 설정이어도 이번 접속에는 쓴다.
            var typed = string.IsNullOrEmpty(editor.Password) ? null : editor.Password;
            var profile = editor.IsNew || editor.IsDirty ? await SaveAndGetAsync().ConfigureAwait(true) : editor.Original;
            if (profile is null)
            {
                return;
            }

            var outcome = await ConnectWithPromptsAsync(profile, typed).ConfigureAwait(true);
            if (outcome?.IsConnected == true)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>접속을 시도하고, 비밀번호가 필요하거나 만료되면 묻는다. 취소하면 null.</summary>
    public async Task<ConnectOutcome?> ConnectWithPromptsAsync(ConnectionProfile profile, string? password = null)
    {
        var typed = password;
        var changedOnce = false;
        while (true)
        {
            var outcome = await _service.ConnectAsync(profile, typed, CancellationToken.None).ConfigureAwait(true);
            LastOutcome = outcome;
            switch (outcome.Status)
            {
                case ConnectStatus.Connected:
                    SetStatus(StatusKind.Info, outcome.Message);
                    return outcome;
                case ConnectStatus.PasswordRequired:
                    if (AskPassword is null || await AskPassword(profile).ConfigureAwait(true) is not { } pw)
                    {
                        SetStatus(StatusKind.Info, "접속을 취소했습니다.");
                        return null;
                    }

                    typed = pw;
                    break;
                case ConnectStatus.PasswordExpired when !changedOnce && AskChangePassword is not null:
                    var request = await AskChangePassword(profile, typed).ConfigureAwait(true);
                    if (request is not { } change)
                    {
                        SetStatus(StatusKind.Error, outcome.Message);
                        return null;
                    }

                    var result = await _service.ChangePasswordAsync(profile, change.Old ?? typed, change.New, CancellationToken.None).ConfigureAwait(true);
                    if (!result.Succeeded)
                    {
                        SetStatus(StatusKind.Error, result.Message);
                        return null;
                    }

                    changedOnce = true;
                    typed = change.New;
                    break;
                default:
                    SetStatus(StatusKind.Error, outcome.Message);
                    return outcome;
            }
        }
    }

    /// <summary>ADB 지갑 zip 을 끌어 놓았을 때: 프로필을 만들고 선택한 뒤, 비밀번호를 물어 접속한다.</summary>
    public async Task ImportWalletAsync(string zipPath, bool thenConnect = true)
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            ConnectionProfile created;
            try
            {
                created = await _service.ImportWalletAsync(zipPath, null, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is WalletFormatException or ConnectionValidationException or System.IO.IOException or UnauthorizedAccessException)
            {
                SetStatus(StatusKind.Error, ex.Message);
                return;
            }

            var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild(doc.Value, created.Id);
            SetStatus(StatusKind.Info, $"지갑으로 접속 '{created.Name}' 을(를) 만들었습니다. 사용자와 비밀번호를 확인하세요.");

            if (thenConnect)
            {
                var outcome = await ConnectWithPromptsAsync(created).ConfigureAwait(true);
                if (outcome?.IsConnected == true)
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>마지막 접속(<see cref="IConnectionService.LastProfile"/>)으로 다시 접속한다. 비밀번호가 필요하면 묻는다.</summary>
    public async Task<ConnectOutcome?> ReconnectAsync()
    {
        if (_service.LastProfile is not { } last)
        {
            SetStatus(StatusKind.Error, "다시 접속할 접속 정보가 없습니다.");
            return null;
        }

        // 저장소의 최신 값(이름·주소를 고쳤을 수 있다)을 우선한다.
        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        var profile = doc.Value.Profiles.FirstOrDefault(p => p.Id == last.Id) ?? last;
        return await ConnectWithPromptsAsync(profile).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedChanged(ConnectionTreeNode? oldValue, ConnectionTreeNode? newValue)
    {
        if (_rebuilding || newValue?.Profile is not { } profile)
        {
            // 폴더를 고르거나 트리를 다시 만드는 중이면 편집기를 그대로 둔다.
            return;
        }

        if (Editor is { IsNew: false } current && current.Original.Id == profile.Id)
        {
            return;
        }

        if (Editor is { IsDirty: true })
        {
            PendingSelection = SwitchAfterAskingAsync(profile.Id);
            return;
        }

        Editor = ProfileEditorViewModel.From(profile);
    }

    /// <summary>저장 안 한 편집을 정리(저장·버리기)한 뒤 고른 접속으로 바꾼다. 취소하면 앞의 선택으로 되돌린다.</summary>
    private async Task SwitchAfterAskingAsync(Guid target)
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            SelectQuietly(Editor is { IsNew: false } editor ? FindNode(editor.Original.Id) : null);
            return;
        }

        if (FindNode(target) is { Profile: { } profile } node)
        {
            SelectQuietly(node);
            Editor = ProfileEditorViewModel.From(profile);
        }
    }

    private ConnectionTreeNode? FindNode(Guid id) => AllNodes().FirstOrDefault(n => n.Profile?.Id == id);

    /// <summary>편집기를 바꾸지 않고 트리 선택만 바꾼다.</summary>
    private void SelectQuietly(ConnectionTreeNode? node)
    {
        _rebuilding = true;
        try
        {
            Selected = node;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    partial void OnEditorChanged(ProfileEditorViewModel? oldValue, ProfileEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.KnownGroups = _allFolders;
        }

        UpdateModifiedMarker();
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfileEditorViewModel.IsDirty):
                UpdateModifiedMarker();
                SaveCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ProfileEditorViewModel.HasRequiredFields):
                ConnectCommand.NotifyCanExecuteChanged();
                TestCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>편집 중인 접속 줄에 「저장 안 함」 표시를 붙이거나 뗀다.</summary>
    private void UpdateModifiedMarker()
    {
        var dirtyId = Editor is { IsNew: false, IsDirty: true } editor ? editor.Original.Id : (Guid?)null;
        foreach (var node in AllNodes())
        {
            node.IsModified = node.Profile is { } p && p.Id == dirtyId;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyView(null, reloadEditor: false);

    partial void OnSortOrderChanged(ConnectionSortOrder value) => ApplyView(null, reloadEditor: false);

    /// <summary>찾기 글자가 프로필에 맞는지. 접속 문자열에 호스트·포트·서비스·TNS 별칭이 다 들어 있다.</summary>
    public static bool Matches(ConnectionProfile profile, string? search)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var text = search.Trim();
        return Contains(profile.Name) || Contains(profile.User) || Contains(profile.DataSource) || Contains(profile.Group) || Contains(profile.ProxyUser);

        bool Contains(string? field) => field is not null && field.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>트리 전체 노드(폴더 포함, 위에서 아래 순서).</summary>
    public IEnumerable<ConnectionTreeNode> AllNodes() => Flatten(Tree);

    private static IEnumerable<ConnectionTreeNode> Flatten(IEnumerable<ConnectionTreeNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    private void Rebuild(ConnectionsDocument doc, Guid? select)
    {
        AdoptDocument(doc);
        ApplyView(select, reloadEditor: true);
    }

    private void AdoptDocument(ConnectionsDocument doc)
    {
        _profiles = [.. doc.Profiles];
        _allFolders = ConnectionFolders.All(doc);
        OnPropertyChanged(nameof(AllFolders));
        if (Editor is { } editor)
        {
            editor.KnownGroups = _allFolders;
        }
    }

    /// <summary>
    /// 찾기·정렬·접힘 상태로 트리를 다시 만든다. <paramref name="select"/> 가 없으면 지금 편집 중인 프로필을 다시 고른다.
    /// <paramref name="reloadEditor"/> 가 false 면 편집기(저장 안 한 값 포함)는 그대로 둔다.
    /// <paramref name="selectFolder"/> 가 없고 폴더가 골라져 있었다면 같은 경로의 폴더를 다시 고른다.
    /// </summary>
    private void ApplyView(Guid? select, bool reloadEditor, string? selectFolder = null)
    {
        var keep = select ?? (Editor is { IsNew: false } current ? current.Original.Id : null);
        var keepFolder = selectFolder ?? (select is null && Selected is { IsFolder: true } f ? f.Path : null);
        var filtering = !string.IsNullOrWhiteSpace(SearchText);
        ConnectionTreeNode? selectNode = null;
        ConnectionTreeNode? folderNode = null;
        var visible = _profiles.Where(p => Matches(p, SearchText));
        var ordered = (SortOrder == ConnectionSortOrder.RecentlyConnected
            ? visible.OrderByDescending(p => p.LastConnectedAt.HasValue).ThenByDescending(p => p.LastConnectedAt).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            : visible.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();

        // 찾는 중에는 맞는 접속이 있는 폴더만, 아니면 빈 폴더까지 모두 보인다.
        IReadOnlyList<string> paths = filtering
            ? ConnectionFolders.All(new ConnectionsDocument { Profiles = ordered })
            : _allFolders;

        _rebuilding = true;
        try
        {
            foreach (var folder in Flatten(Tree).Where(n => n.IsFolder))
            {
                folder.PropertyChanged -= OnFolderChanged;
            }

            Tree.Clear();
            // 폴더는 자식을 다 채운 뒤에 트리에 넣는다(화면이 폴더 줄의 개수 글자를 한 번에 읽는다).
            // 같은 단계에서는 폴더가 위(이름순), 접속이 아래(정렬 선택대로).
            foreach (var path in ChildFolders(paths, null))
            {
                Tree.Add(BuildFolder(path));
            }

            foreach (var p in ordered.Where(p => ConnectionFolders.Normalize(p.Group) is null))
            {
                Tree.Add(NewProfileNode(p));
            }

            Selected = selectNode ?? folderNode;
        }
        finally
        {
            _rebuilding = false;
        }

        // 찾기 글자에 걸려 목록에서 빠진 접속이어도 편집기는 저장된 값으로 다시 채운다(저장 뒤 변경 표시가 남지 않게).
        if (reloadEditor && (selectNode?.Profile ?? _profiles.FirstOrDefault(p => p.Id == keep)) is { } profile)
        {
            Editor = ProfileEditorViewModel.From(profile);
        }

        UpdateModifiedMarker();

        ConnectionTreeNode NewProfileNode(ConnectionProfile p)
        {
            var node = new ConnectionTreeNode(p.Name, p);
            if (p.Id == keep)
            {
                selectNode = node;
            }

            return node;
        }

        ConnectionTreeNode BuildFolder(string path)
        {
            var folder = new ConnectionTreeNode(ConnectionFolders.NameOf(path), null, path);
            foreach (var child in ChildFolders(paths, path))
            {
                folder.Children.Add(BuildFolder(child));
            }

            foreach (var p in ordered.Where(p => ConnectionFolders.PathEquals(ConnectionFolders.Normalize(p.Group), path)))
            {
                folder.Children.Add(NewProfileNode(p));
            }

            folder.ProfileCount = folder.Children.Sum(c => c.IsFolder ? c.ProfileCount : 1);
            if (keepFolder is not null && ConnectionFolders.PathEquals(keepFolder, path))
            {
                folderNode = folder;
            }

            // 찾는 중에는 맞는 접속이 보이도록 모두 펼친다(사용자가 접어 둔 상태는 바꾸지 않는다).
            folder.IsExpanded = filtering || !_collapsed.Contains(path)
                || (selectNode is not null && Flatten(folder.Children).Contains(selectNode))
                || (folderNode is not null && Flatten(folder.Children).Contains(folderNode));
            folder.PropertyChanged += OnFolderChanged;
            return folder;
        }
    }

    /// <summary><paramref name="parent"/> 바로 아래 폴더 경로, 이름순(null = 맨 위).</summary>
    private static IEnumerable<string> ChildFolders(IReadOnlyList<string> paths, string? parent) =>
        paths.Where(p => ConnectionFolders.PathEquals(ConnectionFolders.Parent(p), parent))
            .OrderBy(ConnectionFolders.NameOf, StringComparer.CurrentCultureIgnoreCase);

    private void OnFolderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_rebuilding || e.PropertyName != nameof(ConnectionTreeNode.IsExpanded) || sender is not ConnectionTreeNode folder || !string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        if (folder.IsExpanded)
        {
            _collapsed.Remove(folder.Path);
        }
        else
        {
            _collapsed.Add(folder.Path);
        }
    }

    // ---- 폴더 ----

    /// <summary>
    /// 새 폴더를 만들고 이름 편집 상태로 둔다. 폴더를 골랐으면 그 안, 접속을 골랐으면 그 접속의 폴더 안, 아니면 맨 위.
    /// 이름은 「새 폴더」, 겹치면 「새 폴더 (2)」.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditFolders))]
    private async Task NewFolderAsync()
    {
        var parent = Selected switch
        {
            { IsFolder: true } folder => folder.Path,
            { Profile: { } p } => ConnectionFolders.Normalize(p.Group),
            _ => null,
        };

        string? created = null;
        if (!await TryEditFoldersAsync(doc =>
            {
                created = ConnectionFolders.Combine(parent, ConnectionFolders.UniqueName(ConnectionFolders.All(doc), parent));
                return ConnectionFolders.Create(doc, created);
            }).ConfigureAwait(true) || created is null)
        {
            return;
        }

        await ReloadAsync(null, created, created).ConfigureAwait(true);
        if (FindFolder(created) is { } node)
        {
            BeginEdit(node);
        }

        SetStatus(StatusKind.Info, $"폴더 '{ConnectionFolders.NameOf(created)}' 을(를) 만들었습니다. 이름을 입력하고 Enter 를 누르세요.");
    }

    private bool CanEditFolders() => !IsBusy;

    /// <summary>고른 폴더의 이름 편집을 시작한다(F2).</summary>
    [RelayCommand(CanExecute = nameof(CanRenameFolder))]
    private void RenameFolder()
    {
        if (Selected is { IsFolder: true } folder)
        {
            BeginEdit(folder);
        }
    }

    private bool CanRenameFolder() => Selected is { IsFolder: true } && !IsBusy;

    private void BeginEdit(ConnectionTreeNode folder)
    {
        folder.EditText = folder.Title;
        folder.IsEditing = true;
        FolderEditRequested?.Invoke(this, folder);
    }

    /// <summary>입력칸의 이름으로 폴더 이름을 바꾼다(Enter·초점 잃음). 안의 접속과 하위 폴더 경로가 함께 바뀐다.</summary>
    public async Task CommitFolderEditAsync(ConnectionTreeNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsEditing)
        {
            return;
        }

        folder.IsEditing = false;
        var name = folder.EditText.Trim();
        if (name == folder.Title)
        {
            return;
        }

        if (!ConnectionFolders.IsValidName(name))
        {
            SetStatus(StatusKind.Error, $"폴더 이름은 비어 있거나 '{ConnectionFolders.Separator}' 를 포함할 수 없습니다.");
            return;
        }

        await MoveFolderAsync(folder.Path, ConnectionFolders.Combine(ConnectionFolders.Parent(folder.Path), name)).ConfigureAwait(true);
    }

    /// <summary>이름 편집을 취소한다(Esc). 폴더는 지금 이름 그대로 남는다.</summary>
    public static void CancelFolderEdit(ConnectionTreeNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        folder.IsEditing = false;
    }

    private async Task DeleteFolderAsync(ConnectionTreeNode folder)
    {
        var path = folder.Path;
        var profiles = _profiles.Count(p => ConnectionFolders.IsSameOrUnder(ConnectionFolders.Normalize(p.Group), path));
        var subfolders = _allFolders.Count(f => ConnectionFolders.IsSameOrUnder(f, path) && !ConnectionFolders.PathEquals(f, path));
        if (profiles + subfolders > 0)
        {
            var where = ConnectionFolders.Parent(path) is { } up ? $"'{up}' 폴더" : "맨 위";
            var ok = Confirm is null || await Confirm($"'{path}' 폴더를 지울까요? 안의 접속 {profiles}개와 하위 폴더는 지우지 않고 {where}(으)로 옮깁니다.").ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }

        if (!await ResolveUnsavedIfInFolderAsync(path).ConfigureAwait(true)
            || !await TryEditFoldersAsync(doc => ConnectionFolders.Delete(doc, path)).ConfigureAwait(true))
        {
            return;
        }

        await ReloadAsync(null, null, ConnectionFolders.Parent(path)).ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"폴더 '{path}' 을(를) 지웠습니다.");
    }

    /// <summary>「폴더로 이동…」: 폴더 목록(「(폴더 없음)」 포함)에서 골라 고른 줄을 옮긴다.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToFolder))]
    private async Task MoveToFolderAsync()
    {
        if (Selected is not { } node || AskFolder is null)
        {
            return;
        }

        var candidates = _allFolders.Where(f => node.IsFolder is false || !ConnectionFolders.IsSameOrUnder(f, node.Path)).ToList();
        var current = node.IsFolder ? ConnectionFolders.Parent(node.Path) : ConnectionFolders.Normalize(node.Profile?.Group);
        if (await AskFolder(candidates, current).ConfigureAwait(true) is { } choice)
        {
            await MoveNodeAsync(node, choice.Path).ConfigureAwait(true);
        }
    }

    private bool CanMoveToFolder() => HasAnySelection && !IsBusy && AskFolder is not null;

    /// <summary>끌어 놓을 수 있는지: 접속은 다른 폴더로만, 폴더는 자기·자기 하위·지금 부모가 아닌 곳으로만.</summary>
    public static bool CanDrop(ConnectionTreeNode node, string? targetFolder)
    {
        ArgumentNullException.ThrowIfNull(node);
        var target = ConnectionFolders.Normalize(targetFolder);
        return node.IsFolder
            ? !ConnectionFolders.IsSameOrUnder(target, node.Path) && !ConnectionFolders.PathEquals(ConnectionFolders.Parent(node.Path), target)
            : !ConnectionFolders.PathEquals(ConnectionFolders.Normalize(node.Profile?.Group), target);
    }

    /// <summary>접속 또는 폴더를 폴더로 옮긴다(끌어 놓기·「폴더로 이동」). <paramref name="targetFolder"/> 가 null 이면 맨 위.</summary>
    public async Task<bool> MoveNodeAsync(ConnectionTreeNode node, string? targetFolder)
    {
        ArgumentNullException.ThrowIfNull(node);
        var target = ConnectionFolders.Normalize(targetFolder);
        if (!CanDrop(node, target))
        {
            if (node.IsFolder && ConnectionFolders.IsSameOrUnder(target, node.Path))
            {
                SetStatus(StatusKind.Error, "폴더를 자기 자신이나 그 하위로 옮길 수 없습니다.");
            }

            return false;
        }

        if (node.Profile is { } profile)
        {
            if (Editor is { IsNew: false, IsDirty: true } editor && editor.Original.Id == profile.Id && !await ResolveUnsavedAsync().ConfigureAwait(true))
            {
                return false;
            }

            if (!await TryEditFoldersAsync(doc => ConnectionFolders.MoveProfile(doc, profile.Id, target)).ConfigureAwait(true))
            {
                return false;
            }

            await ReloadAsync(profile.Id, null, target).ConfigureAwait(true);
            SetStatus(StatusKind.Info, $"'{profile.Name}' 을(를) {(target is null ? "맨 위" : $"'{target}' 폴더")}(으)로 옮겼습니다.");
            return true;
        }

        return await MoveFolderAsync(node.Path, ConnectionFolders.Combine(target, node.Title)).ConfigureAwait(true);
    }

    private async Task<bool> MoveFolderAsync(string from, string to)
    {
        if (!await ResolveUnsavedIfInFolderAsync(from).ConfigureAwait(true)
            || !await TryEditFoldersAsync(doc => ConnectionFolders.Move(doc, from, to)).ConfigureAwait(true))
        {
            return false;
        }

        await ReloadAsync(null, to, ConnectionFolders.Parent(to)).ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"폴더 '{from}' 을(를) '{to}' (으)로 바꿨습니다.");
        return true;
    }

    /// <summary>편집 중인 접속이 폴더 안에 있고 저장 안 한 값이 있으면 먼저 정리한다(폴더가 바뀌면 편집 값의 그룹이 어긋난다).</summary>
    private async Task<bool> ResolveUnsavedIfInFolderAsync(string folderPath) =>
        Editor is not { IsNew: false, IsDirty: true } editor
        || !ConnectionFolders.IsSameOrUnder(ConnectionFolders.Normalize(editor.Original.Group), folderPath)
        || await ResolveUnsavedAsync().ConfigureAwait(true);

    private async Task<bool> TryEditFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit)
    {
        try
        {
            await _service.UpdateFoldersAsync(edit, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is ConnectionValidationException or ConnectionFolderException or System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return false;
        }
    }

    /// <summary>저장소를 다시 읽어 트리를 만들고 지정한 줄을 고른다. <paramref name="reveal"/> 폴더와 그 상위 폴더는 펼친다.</summary>
    private async Task ReloadAsync(Guid? selectProfile, string? selectFolder, string? reveal)
    {
        for (var path = reveal; path is not null; path = ConnectionFolders.Parent(path))
        {
            _collapsed.Remove(path);
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        FileNotice = doc.Notice;
        AdoptDocument(doc.Value);

        // 저장 안 한 편집이 없을 때만 편집기를 저장된 값으로 다시 채운다(옮긴 접속의 그룹 칸이 바로 바뀌게).
        ApplyView(selectProfile, reloadEditor: Editor is not { IsDirty: true }, selectFolder);
    }

    /// <summary>경로의 폴더 줄(없으면 null).</summary>
    public ConnectionTreeNode? FindFolder(string path) => AllNodes().FirstOrDefault(n => n.IsFolder && ConnectionFolders.PathEquals(n.Path, path));

    /// <summary>화면 코드가 상태 줄에 오류를 알린다.</summary>
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
