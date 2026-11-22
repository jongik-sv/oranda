using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;

namespace Oranda.App.Features.AiSetup.Server;

/// <summary>
/// 「회사 AI 서버」 단계 화면 모델. 두 길:
/// <list type="bullet">
/// <item>설정 파일(.oranda-ai): <see cref="AiSetupFile.Parse"/> 로 읽고(키 칸이 든 파일은 거절), 「연결하기」 로 저장·활성. 모델이 없으면 서버에 물어 고른다.</item>
/// <item>주소만 받음: 주소 + 열쇠 → 「모델 새로고침」 이 첫 모델을 고르고, 「연결하기」 로 저장·활성.</item>
/// </list>
/// 직원 화면에는 로컬 AI·API 형식·토큰 칸을 두지 않는다(로컬 AI 는 파일에 참일 때만). 열쇠는 바인딩하지 않고 <see cref="KeyProvider"/> 로 읽는다.
/// </summary>
public sealed partial class ServerStepViewModel : ObservableObject, IDisposable
{
    private readonly ServerProfileSaver _saver;
    private readonly IShellText? _shellText;
    private readonly ServerText _t;
    private ServerDraft? _fileDraft;
    private AiApiFormat _foundFormat = AiApiFormat.OpenAi;
    private Guid? _savedButInactive;
    private Func<string>? _error;
    private Func<string>? _status;

    public ServerStepViewModel(ServerProfileSaver saver, IShellText? text)
    {
        ArgumentNullException.ThrowIfNull(saver);
        _saver = saver;
        _shellText = text;
        _t = new ServerText(text);
        if (text is not null)
        {
            text.LanguageChanged += OnLanguageChanged;
        }
    }

    /// <summary>열쇠 칸의 지금 글(화면이 연결). 비면 열쇠 없이 저장한다.</summary>
    public Func<string?>? KeyProvider { get; set; }

    /// <summary>저장에 성공해 열쇠 칸을 비워도 된다.</summary>
    public event EventHandler? KeyConsumed;

    public ObservableCollection<string> Models { get; } = [];

    /// <summary>주소만 받은 길로 바꿨다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileMode), nameof(CanConnect))]
    private bool _isAddressMode;

    public bool IsFileMode => !IsAddressMode;

    /// <summary>읽은 설정 파일이 있다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _fileLoaded;

    [ObservableProperty]
    private string _fileSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlainTextWarning), nameof(CanConnect))]
    private string _address = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private string? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>도는 연결 작업(단계를 떠날 때 기다린다).</summary>
    public Task LastConnect { get; private set; } = Task.CompletedTask;

    /// <summary>프로필을 저장하고 활성으로 골라 이 단계가 끝났다. 끝난 뒤에는 입력(파일·주소)을 바꿔야 다시 연결한다(같은 프로필이 둘 생기지 않게).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isComplete;

    /// <summary>만든 프로필 id(연결 시험·이어 하기가 쓴다).</summary>
    [ObservableProperty]
    private Guid? _createdProfileId;

    public bool ShowPlainTextWarning => ServerProfileSaver.IsPlainText(IsAddressMode ? Address : _fileDraft?.BaseUrl);

    public bool CanConnect => !IsBusy && !IsComplete && (IsAddressMode ? Address.Trim().Length > 0 && !string.IsNullOrWhiteSpace(SelectedModel) : FileLoaded);

    // ------------------------------------------------------------------ 화면 글

    public string Title => _t.T("회사 AI 서버 연결", "Connect to the company AI server");

    public string Intro => _t.T(
        "담당자에게 받은 설정 파일을 아래에 끌어다 놓으세요. 파일이 없고 주소만 받았으면 「주소만 받았어요」를 누르세요.",
        "Drag the settings file from your administrator onto the box below. If you only got an address, press \"I only have an address\".");

    public string DropText => _t.T("담당자에게 받은 설정 파일(.oranda-ai)을 여기에 놓으세요", "Drop the settings file (.oranda-ai) from your administrator here");

    public string PickLabel => _t.T("파일 고르기", "Choose file");

    public string AddressOnlyLabel => _t.T("주소만 받았어요", "I only have an address");

    public string BackToFileLabel => _t.T("설정 파일이 있어요", "I have a settings file");

    public string AddressLabel => _t.T("서버 주소", "Server address");

    public string AddressPlaceholder => _t.T("https:// 로 시작하는 주소를 붙여 넣으세요", "Paste the address that starts with https://");

    public string KeyLabel => _t.T("열쇠(API 키) — 담당자가 줬을 때만", "Key (API key) — only if your administrator gave you one");

    public string RefreshLabel => _t.T("모델 새로고침", "Refresh models");

    public string ModelLabel => _t.T("모델", "Model");

    public string ConnectLabel => _t.T("연결하기", "Connect");

    public string PlainTextWarning => _t.T(
        "암호화하지 않은 http 주소예요. 담당자가 준 주소가 맞는지 확인하세요.",
        "This is an unencrypted http address. Check that it is the address your administrator gave you.");

    // ------------------------------------------------------------------ 동작

    /// <summary>설정 파일 경로를 읽는다(끌어다 놓기·파일 고르기). 너무 크거나 못 읽으면 거절 글.</summary>
    public async Task LoadFileAsync(string path, CancellationToken ct)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > AiSetupFile.MaxLength * 4L)
            {
                ShowFileError(AiSetupFileError.NotReadable);
                return;
            }

            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowFileError(AiSetupFileError.NotReadable);
            return;
        }

        LoadFileText(text);
    }

    /// <summary>설정 파일 글을 읽는다. 열쇠 칸이 든 파일·잘못된 주소 등은 거절 글만 보이고 아무것도 저장하지 않는다.</summary>
    public void LoadFileText(string? text)
    {
        var result = AiSetupFile.Parse(text);
        if (result.Profile is not { } profile)
        {
            ShowFileError(result.Error ?? AiSetupFileError.NotReadable);
            return;
        }

        _fileDraft = new ServerDraft(profile.Name, profile.BaseUrl, profile.Format, string.IsNullOrWhiteSpace(profile.Model) ? null : profile.Model, profile.IsLocalAi);
        IsAddressMode = false;
        FileLoaded = true;
        IsComplete = false;
        _savedButInactive = null;
        RefreshSummary();
        ShowError(null);
        OnPropertyChanged(nameof(ShowPlainTextWarning));
    }

    partial void OnAddressChanged(string value)
    {
        // 다른 주소면 앞서 찾은 모델·형식은 맞지 않는다: 다시 새로고침하게 한다.
        Models.Clear();
        SelectedModel = null;
        _foundFormat = AiApiFormat.OpenAi;
        _savedButInactive = null;
        IsComplete = false;
    }

    public void UseAddressOnly()
    {
        IsAddressMode = true;
        ShowError(null);
        OnPropertyChanged(nameof(ShowPlainTextWarning));
    }

    public void UseFile()
    {
        IsAddressMode = false;
        ShowError(null);
        OnPropertyChanged(nameof(ShowPlainTextWarning));
    }

    /// <summary>주소만 받은 길: 서버에 모델 목록을 묻고 첫 모델을 고른다.</summary>
    public async Task RefreshModelsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ShowError(null);
        ShowStatus(() => _t.T("서버에 묻는 중…", "Asking the server…"));
        try
        {
            var found = await _saver.FindModelsAsync(Address, KeyProvider?.Invoke(), SelectedModel, CancellationToken.None).ConfigureAwait(true);
            Models.Clear();
            if (!found.Success)
            {
                SelectedModel = null;
                var message = found.Error;
                ShowError(() => message);
                return;
            }

            foreach (var model in found.Models)
            {
                Models.Add(model);
            }

            _foundFormat = found.Format;
            SelectedModel = found.Chosen;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Models.Clear();
            SelectedModel = null;
            ShowError(() => _t.ModelsFailed);
            System.Diagnostics.Trace.TraceWarning("AI 서버 모델 목록 실패: {0}", ex.GetType().Name);
        }
        finally
        {
            ShowStatus(null);
            IsBusy = false;
        }
    }

    /// <summary>「연결하기」: 저장하고 활성으로 고른다. 저장 뒤 활성만 실패했으면 다시 누를 때 활성만 다시 한다.</summary>
    public Task ConnectAsync()
    {
        if (IsBusy || IsComplete || (!CanConnect && _savedButInactive is null))
        {
            return Task.CompletedTask;
        }

        LastConnect = ConnectCoreAsync();
        return LastConnect;
    }

    private async Task ConnectCoreAsync()
    {
        IsBusy = true;
        ShowError(null);
        try
        {
            ServerSaveResult result;
            if (_savedButInactive is { } pending)
            {
                result = await _saver.ActivateAsync(pending, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                if (await DraftAsync().ConfigureAwait(true) is not { } draft)
                {
                    return;
                }

                result = await _saver.SaveAsync(draft, KeyProvider?.Invoke(), CancellationToken.None).ConfigureAwait(true);
            }

            if (result.Success)
            {
                _savedButInactive = null;
                CreatedProfileId = result.ProfileId;
                KeyConsumed?.Invoke(this, EventArgs.Empty);
                ShowStatus(() => _t.T("연결했어요. 「다음」을 눌러 연결을 시험해 보세요.", "Connected. Press \"Next\" to test the connection."));
                IsComplete = true;
                return;
            }

            _savedButInactive = result.ProfileId;
            if (result.ProfileId is not null)
            {
                KeyConsumed?.Invoke(this, EventArgs.Empty); // 열쇠는 이미 저장됐다
            }

            var message = result.Error;
            ShowError(() => message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(() => _t.ModelsFailed);
            System.Diagnostics.Trace.TraceWarning("AI 서버 연결 실패: {0}", ex.GetType().Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>이어 하기: 앞서 만든 프로필이 아직 있으면 끝난 것으로 둔다.</summary>
    public void Restore(Guid? profileId, AiSettings ai)
    {
        ArgumentNullException.ThrowIfNull(ai);
        if (profileId is { } id && ai.Profiles.FirstOrDefault(p => p.Id == id) is { } profile)
        {
            CreatedProfileId = id;
            FileSummary = Summary(profile.Name, profile.BaseUrl, profile.Model);
            FileLoaded = true;
            IsComplete = true;
        }
    }

    private async Task<ServerDraft?> DraftAsync()
    {
        if (IsAddressMode)
        {
            var host = Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var uri) ? uri.Host : _t.DefaultName;
            return new ServerDraft(host, Address.Trim(), _foundFormat, SelectedModel, LocalAi: false);
        }

        if (_fileDraft is not { } draft)
        {
            return null;
        }

        if (draft.Model is null)
        {
            var found = await _saver.FindModelsAsync(draft.BaseUrl, KeyProvider?.Invoke(), null, CancellationToken.None).ConfigureAwait(true);
            if (!found.Success)
            {
                var message = found.Error;
                ShowError(() => message);
                return null;
            }

            draft = draft with { Model = found.Chosen };
        }

        return draft;
    }

    private void ShowFileError(AiSetupFileError error)
    {
        _fileDraft = null;
        FileLoaded = false;
        FileSummary = string.Empty;
        ShowError(() => AiSetupFile.ErrorText(error, _t.IsEnglish));
    }

    private void RefreshSummary()
    {
        if (_fileDraft is { } d)
        {
            FileSummary = Summary(d.Name, d.BaseUrl, d.Model);
        }
    }

    private string Summary(string name, string baseUrl, string? model) =>
        _t.T("이름", "Name") + ": " + name + "\n"
        + _t.T("서버", "Server") + ": " + baseUrl + "\n"
        + _t.T("모델", "Model") + ": " + (string.IsNullOrWhiteSpace(model) ? _t.T("서버에 물어 고름", "chosen from the server") : model);

    private void ShowStatus(Func<string>? status)
    {
        _status = status;
        StatusText = status?.Invoke() ?? string.Empty;
    }

    private void ShowError(Func<string>? error)
    {
        _error = error;
        ErrorText = error?.Invoke() ?? string.Empty;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[]
        {
            nameof(Title), nameof(Intro), nameof(DropText), nameof(PickLabel), nameof(AddressOnlyLabel), nameof(BackToFileLabel), nameof(AddressLabel),
            nameof(AddressPlaceholder), nameof(KeyLabel), nameof(RefreshLabel), nameof(ModelLabel), nameof(ConnectLabel), nameof(PlainTextWarning),
        })
        {
            OnPropertyChanged(name);
        }

        RefreshSummary();
        if (_status is { } status)
        {
            StatusText = status();
        }

        if (_error is { } error)
        {
            ErrorText = error();
        }
    }

    public void Dispose()
    {
        if (_shellText is not null)
        {
            _shellText.LanguageChanged -= OnLanguageChanged;
        }
    }
}
