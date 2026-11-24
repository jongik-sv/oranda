using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>접속 읽기·저장 길. 앱에서는 <see cref="FromConnections"/> 로 만든다(비밀번호는 건드리지 않는다).</summary>
/// <param name="Load">접속 목록.</param>
/// <param name="Save">접속 하나 저장(저장된 접속을 돌려준다).</param>
/// <param name="SaveConfirmed">운영 수준을 직접 올릴 때 다시 입력한 접속 이름과 함께 저장(공유 서비스가 접속 편집과 같은 규칙으로 다시 본다). null 이면 <paramref name="Save"/>.</param>
public sealed record AccessStepServices(
    Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> Load,
    Func<ConnectionProfile, CancellationToken, Task<ConnectionProfile>> Save,
    Func<ConnectionProfile, string?, CancellationToken, Task<ConnectionProfile>>? SaveConfirmed = null)
{
    /// <summary>
    /// 접속 서비스로 만든다. 저장은 비밀번호를 넘기지 않고 「저장된 비밀번호 유지」 여부만 지금대로 둔다
    /// (본보기 = <c>ExternalAgentConnectCommands.ServicesFrom</c>).
    /// </summary>
    public static AccessStepServices FromConnections(IConnectionService connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return new(
            async ct => (await connections.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles.ToList(),
            (profile, ct) => connections.SaveProfileAsync(profile, password: null, savePassword: profile.CredentialRef is not null, ct));
    }

    /// <summary>
    /// 공유 허용 수준 서비스로 만든다(ai-settings-one-place): 켬/끔은 그 실효 수준으로 설정 「AI」 탭·외부 에이전트 연결 창과 같은 한 길에 저장한다.
    /// 저장 꼴은 <see cref="FromConnections"/> 와 같다(켬 = 환경 기본값 = null, 끔 = Off) — 화면·동작은 그대로다.
    /// </summary>
    public static AccessStepServices FromShared(SharedAiAccessService shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        return new(shared.LoadProfilesAsync, (profile, ct) => shared.SaveDesiredAsync(profile, confirmName: null, ct), shared.SaveDesiredAsync);
    }
}

/// <summary>
/// 「AI 를 쓸 접속」 단계. 접속마다 켬/끔 스위치만 둔다. 저장하면 바뀐 줄만 저장하고, 켠 접속 목록을 다음 단계(해 보기)에 넘긴다.
/// 모두 끄거나 접속이 없으면 끝나되 해 보기는 건너뛴다.
/// </summary>
public sealed partial class AccessStepViewModel : ObservableObject, IDisposable
{
    private readonly AccessStepServices _services;
    private readonly IShellText? _shellText;
    private readonly AccessText _t;
    private bool _loaded;

    public AccessStepViewModel(AccessStepServices services, IShellText? text)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _shellText = text;
        _t = new AccessText(text);
        if (text is not null)
        {
            text.LanguageChanged += OnLanguageChanged;
        }
    }

    public ObservableCollection<AccessRow> Rows { get; } = [];

    public string Title => _t.Title;

    public string Intro => _t.Intro;

    /// <summary>표 아래 한 줄 뜻풀이(수준 다섯).</summary>
    public string Legend => _t.Legend;

    /// <summary>도우미 단계 아래 파란 띠(설정 「AI」 탭 ①과 같은 표).</summary>
    public string SharedNote => _t.SharedNote;

    /// <summary>표의 글을 만드는 쪽(같은 컨트롤을 쓰는 설정 「AI」 탭이 요약 줄에 쓴다).</summary>
    internal AccessText Text => _t;

    public string SkipNoticeText => HasNoConnections ? _t.NoConnections : _t.SkipNotice;

    /// <summary>접속을 읽었는데 하나도 없다.</summary>
    public bool HasNoConnections => _loaded && Rows.Count == 0 && ErrorText.Length == 0;

    /// <summary>켠 접속이 없어 해 보기를 건너뛴다는 안내를 보인다.</summary>
    public bool ShowSkipNotice => _loaded && !_loadFailed && !Rows.Any(r => r.IsOn);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoConnections), nameof(SkipNoticeText))]
    private string _errorText = string.Empty;

    private bool _loadFailed;

    // 지금 오류 글을 만드는 함수(언어가 바뀌면 다시 만든다).
    private Func<string>? _error;

    private void ShowError(Func<string>? error)
    {
        _error = error;
        ErrorText = error?.Invoke() ?? string.Empty;
    }

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>스위치를 바꾸면 바로 저장한다(도우미 화면). 끄면 <see cref="SaveAsync"/> 를 직접 부른다.</summary>
    public bool AutoSave { get; init; }

    /// <summary>자동 저장이 끝났다(시험이 기다린다).</summary>
    public Task LastSave { get; private set; } = Task.CompletedTask;

    /// <summary>저장을 마쳐 이 단계가 끝났다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TryItSkipped))]
    private bool _isComplete;

    /// <summary>저장 뒤 켜진 접속(운영은 맨 뒤). 해 보기 단계가 쓴다.</summary>
    public IReadOnlyList<ConnectionProfile> EnabledProfiles { get; private set; } = [];

    /// <summary>끝났는데 켠 접속이 없어 해 보기를 건너뛴다.</summary>
    public bool TryItSkipped => IsComplete && EnabledProfiles.Count == 0;

    public async Task LoadAsync(CancellationToken ct)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        ShowError(null);
        _loaded = false;
        _loadFailed = false;
        EnabledProfiles = [];
        OnPropertyChanged(nameof(EnabledProfiles));
        IsComplete = false;
        IsBusy = true;
        try
        {
            var profiles = await _services.Load(ct).ConfigureAwait(true);
            foreach (var profile in profiles)
            {
                var row = new AccessRow(profile, _t);
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loadFailed = true;
            var message = ex.Message;
            ShowError(() => _t.LoadFailed + message);
        }
        finally
        {
            IsBusy = false;
            _loaded = true;
            Notify();
        }
    }

    /// <summary>바뀐 줄을 저장한다. 하나라도 실패하면 이유를 보이고 false(이미 저장한 줄은 저장된 것으로 둔다).</summary>
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (!_loaded || _loadFailed || IsBusy)
        {
            return false;
        }

        ShowError(null);
        IsBusy = true;
        try
        {
            // 저장하는 사이 스위치를 또 바꿨으면 그 줄도 저장한다: 바뀐 줄이 남지 않을 때까지 돈다.
            // 저장 결과가 스위치와 끝내 맞지 않는 경우(저장소가 값을 바꿔 돌려줌)에 끝없이 돌지 않게 횟수를 막는다.
            var rounds = (Rows.Count * 2) + 2;
            // 운영 수준을 직접 올렸는데 이름을 아직 다시 입력하지 않은 줄은 저장하지 않고 기다린다(이름이 맞으면 그때 저장).
            while (rounds-- > 0 && Rows.FirstOrDefault(r => r.IsChanged && !r.NeedsName) is { } row)
            {
                try
                {
                    var desired = Oranda.Core.Ai.Access.AiConnectionAccessRules.Apply(row.Profile, row.Level)
                        with { AiMaskSensitive = row.MaskSensitive ? null : false }; // D-069: 켬 = null(기본)
                    row.Profile = row.ConfirmName.Length > 0 && _services.SaveConfirmed is { } confirmed
                        ? await confirmed(desired, row.ConfirmName, ct).ConfigureAwait(true)
                        : await _services.Save(desired, ct).ConfigureAwait(true);
                    row.ConfirmName = string.Empty;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var name = row.Name;
                    var message = ex.Message;
                    ShowError(() => _t.SaveFailed(name) + message);
                    return false;
                }
            }

            // 넘기는 목록은 저장된 값 기준이다(화면 스위치가 아니라).
            EnabledProfiles = [.. Rows.Where(r => AccessChoice.IsOn(r.Profile)).OrderBy(r => r.IsProduction).Select(r => r.Profile)];
            OnPropertyChanged(nameof(EnabledProfiles));
            OnPropertyChanged(nameof(Rows)); // 요약 줄(설정 「AI」 탭 ②·③)이 저장된 값으로 다시 그린다
            IsComplete = !Rows.Any(r => r.NeedsName);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccessRow.Level) or nameof(AccessRow.ConfirmName) or nameof(AccessRow.MaskSensitive))
        {
            IsComplete = false; // 스위치를 바꾸면 다시 저장해야 끝난다
            Notify();
            if (AutoSave && !IsBusy)
            {
                LastSave = SaveAsync(CancellationToken.None); // 저장 중이면 도는 저장이 바뀐 줄까지 저장한다
            }
        }
    }

    /// <summary>
    /// 다른 곳(외부 에이전트 연결 창·접속 편집·도우미)에서 저장한 값으로 맞춘다. 바꾸는 중인 줄은 그대로 둔다. 접속이 늘거나 줄었으면 다시 읽는다.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!_loaded || IsBusy)
        {
            return;
        }

        IReadOnlyList<ConnectionProfile> profiles;
        try
        {
            profiles = await _services.Load(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // 다음에 다시 맞춘다
        }

        if (profiles.Count != Rows.Count || profiles.Select(p => p.Id).Except(Rows.Select(r => r.Profile.Id)).Any())
        {
            await LoadAsync(ct).ConfigureAwait(true);
            return;
        }

        foreach (var profile in profiles)
        {
            if (Rows.First(r => r.Profile.Id == profile.Id) is { IsChanged: false } row)
            {
                row.Reset(profile);
            }
        }

        EnabledProfiles = [.. Rows.Where(r => AccessChoice.IsOn(r.Profile)).OrderBy(r => r.IsProduction).Select(r => r.Profile)];
        OnPropertyChanged(nameof(EnabledProfiles));
        OnPropertyChanged(nameof(Rows));
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(ShowSkipNotice));
        OnPropertyChanged(nameof(SkipNoticeText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(Legend));
        OnPropertyChanged(nameof(SharedNote));
        OnPropertyChanged(nameof(SkipNoticeText));
        if (_error is { } error)
        {
            ErrorText = error();
        }

        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    public void Dispose()
    {
        if (_shellText is not null)
        {
            _shellText.LanguageChanged -= OnLanguageChanged;
        }

        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }
    }
}
