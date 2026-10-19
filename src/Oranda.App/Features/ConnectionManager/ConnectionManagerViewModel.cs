using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속 트리의 한 줄: 폴더(그룹) 또는 접속.</summary>
public sealed class ConnectionTreeNode
{
    public ConnectionTreeNode(string title, ConnectionProfile? profile = null)
    {
        Title = title;
        Profile = profile;
    }

    public string Title { get; }

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

    public string Subtitle => Profile is null ? string.Empty : $"{Profile.User}@{Profile.DataSource}";
}

/// <summary>
/// 접속 관리자 대화상자의 모델(Ctrl+Shift+O): 접속 트리, 프로필 편집, 접속, 지갑 가져오기(CONN-01~04, 06, 08).
/// 비밀번호 입력·확인 같은 화면 작업은 <see cref="AskPassword"/>·<see cref="AskChangePassword"/>·<see cref="Confirm"/> 로 화면이 맡는다.
/// </summary>
public sealed partial class ConnectionManagerViewModel : ViewModelBase
{
    private readonly IConnectionService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ConnectCommand))]
    private ConnectionTreeNode? _selected;

    [ObservableProperty]
    private ProfileEditorViewModel? _editor;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsError))]
    private StatusKind _statusKind;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _fileNotice;

    public ConnectionManagerViewModel(IConnectionService service)
    {
        _service = service;
    }

    public ObservableCollection<ConnectionTreeNode> Tree { get; } = [];

    /// <summary>접속 성공 뒤 대화상자를 닫으라는 신호.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>비밀번호를 묻는다(접속 이름 → 입력값, 취소면 null).</summary>
    public Func<ConnectionProfile, Task<string?>>? AskPassword { get; set; }

    /// <summary>비밀번호 변경 대화상자(접속 → (현재, 새) 비밀번호, 취소면 null). 현재 비밀번호를 비워 두면 저장된 값을 쓴다.</summary>
    public Func<ConnectionProfile, string?, Task<(string? Old, string New)?>>? AskChangePassword { get; set; }

    /// <summary>삭제 같은 되돌릴 수 없는 작업 확인(메시지 → 예/아니오). 지정하지 않으면 예.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    public bool HasSelection => Selected?.Profile is not null;

    public bool StatusIsError => StatusKind == StatusKind.Error;

    /// <summary>접속에 성공한 마지막 결과(시험·호출자용).</summary>
    public ConnectOutcome? LastOutcome { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var doc = await _service.LoadAsync(ct).ConfigureAwait(true);
        FileNotice = doc.Notice;
        Rebuild(doc.Value.Profiles, null);
    }

    [RelayCommand]
    private void AddNew()
    {
        Selected = null;
        Editor = ProfileEditorViewModel.ForNew();
        SetStatus(StatusKind.Info, "새 접속을 입력하세요.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
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

    [RelayCommand]
    private async Task SaveAsync() => await SaveCurrentAsync().ConfigureAwait(true);

    /// <summary>편집 중인 내용을 저장한다. 성공하면 true.</summary>
    public async Task<bool> SaveCurrentAsync()
    {
        if (Editor is null)
        {
            return false;
        }

        var profile = Editor.TryBuild(out var errors);
        if (profile is null)
        {
            SetStatus(StatusKind.Error, string.Join(Environment.NewLine, errors));
            return false;
        }

        try
        {
            var saved = await _service.SaveProfileAsync(profile, Editor.Password, Editor.SavePassword, CancellationToken.None).ConfigureAwait(true);
            Editor.Password = string.Empty;
            var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild(doc.Value.Profiles, saved.Id);
            SetStatus(StatusKind.Info, $"'{saved.Name}' 을(를) 저장했습니다.");
            return true;
        }
        catch (ConnectionValidationException ex)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return false;
        }
    }

    /// <summary>편집 내용을 저장하고(필요하면) 접속한다. 성공하면 <see cref="CloseRequested"/>.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ConnectAsync()
    {
        if (Editor is null || Selected?.Profile is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!await SaveCurrentAsync().ConfigureAwait(true))
            {
                return;
            }

            var profile = Selected?.Profile;
            if (profile is null)
            {
                return;
            }

            var outcome = await ConnectWithPromptsAsync(profile).ConfigureAwait(true);
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
    public async Task<ConnectOutcome?> ConnectWithPromptsAsync(ConnectionProfile profile)
    {
        string? typed = null;
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
            Rebuild(doc.Value.Profiles, created.Id);
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

    partial void OnSelectedChanged(ConnectionTreeNode? value)
    {
        if (value?.Profile is { } profile)
        {
            Editor = ProfileEditorViewModel.From(profile);
        }
    }

    private void Rebuild(IReadOnlyList<ConnectionProfile> profiles, Guid? select)
    {
        Tree.Clear();
        ConnectionTreeNode? selectNode = null;
        foreach (var group in profiles.GroupBy(p => p.Group ?? string.Empty).OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            ConnectionTreeNode? folder = null;
            if (group.Key.Length > 0)
            {
                folder = new ConnectionTreeNode(group.Key);
                Tree.Add(folder);
            }

            foreach (var p in group)
            {
                var node = new ConnectionTreeNode(p.Name, p);
                (folder?.Children ?? Tree).Add(node);
                if (p.Id == select)
                {
                    selectNode = node;
                }
            }
        }

        if (selectNode is not null)
        {
            Selected = selectNode;
            Editor = ProfileEditorViewModel.From(selectNode.Profile!);
        }
        else if (Selected is not null)
        {
            Selected = null;
        }
    }

    private void SetStatus(StatusKind kind, string text)
    {
        StatusKind = kind;
        Status = text;
    }
}

/// <summary>상태 줄 종류.</summary>
public enum StatusKind
{
    Info,
    Error,
}
