using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Events;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>프리셋 카드 한 장(7개 중 하나).</summary>
public sealed partial class AiPresetCard(AiProfilePresetInfo info, string title, string subtitle) : ObservableObject
{
    public AiProfilePresetInfo Info { get; } = info;

    public AiProfilePreset Preset => Info.Preset;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>왼쪽 프로필 목록 한 줄.</summary>
public sealed partial class AiProfileListItem(Guid id, string name, string subtitle, bool isUsable) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Subtitle { get; } = subtitle;

    /// <summary>false 면 이 빌드가 모르는 프리셋·형식이라 고칠 수 없고 지우기만 된다.</summary>
    public bool IsUsable { get; } = isUsable;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>연결 테스트·모델 새로고침·저장 결과 띠의 종류.</summary>
public enum AiResultKind
{
    None,
    Success,
    Failure,
    Info,
}

/// <summary>
/// LLM 프로필 관리 창(설계 §6.5, 시안 「프로필 관리 창」). 왼쪽 목록과 오른쪽 편집 칸: 프리셋 카드 7개, API 형식(직접 입력만), URL, Key, 모델명, 이미지 입력 지원,
/// 최대 입력·출력 토큰, 프로필 이름, 삭제·취소·연결 테스트·저장.
/// <list type="bullet">
/// <item>저장·삭제는 설정 창 확인과 별개로 즉시 기록한다(<see cref="SettingsService"/> 로 <c>ai.profiles</c> 만). 기록한 뒤 <c>SettingsChanged("ai")</c> 를 직접 발행하고
/// 설정 쪽 페이지에 목록을 새로 알린다. 설정 파일이 읽기 전용이면 말없이 성공한 것처럼 보이지 않게 거부한다.</item>
/// <item>API 키는 <c>ICredentialStore</c> 에만 둔다. 키 입력 칸은 바인딩하지 않고 <see cref="EnteredKeyProvider"/> 로 읽으며, 이 모델은 「저장된 키가 있다」(bool) 만 안다.
/// 기존 키가 있으면 빈 칸은 변경 없음이고 지우기는 <see cref="ClearStoredKeyCommand"/> 로 따로 한다.</item>
/// <item>저장은 키 먼저, 설정 다음이다. 설정 쓰기가 실패하면 방금 쓴 키를 되돌린다(옛 키가 있으면 복원). 삭제는 설정 먼저, 키 다음이다.</item>
/// <item>프리셋을 바꿔도 사용자가 고친 칸은 말없이 잃지 않는다: 손대지 않은 칸만 새 프리셋의 <see cref="AiProfilePresets.NewDraft"/> 값으로 바꾼다.</item>
/// </list>
/// </summary>
public sealed partial class AiProfileManagerViewModel : ObservableObject, IDisposable
{
    private readonly AiProfileServices _services;
    private readonly AiSettingsText _t;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly Action<AiSettings, Guid?, Guid?>? _applied;
    private CancellationTokenSource? _operation;
    private int _version;
    private bool _disposed;
    private bool _attempted;
    private bool _loading;
    private Guid _editingId;
    private bool _isNew;
    private readonly bool _selectNewOnSave;

    /// <param name="services">설정·키 저장소·서버 조회·알림 버스.</param>
    /// <param name="confirm">삭제·변경 버림 확인을 묻는다(true = 진행). 창이 대화 상자를 넘긴다.</param>
    /// <param name="applied">기록 뒤 설정 쪽 페이지에 알린다(최신 설정, 지운 프로필 id, 새로 만든 프로필 id).</param>
    /// <param name="selectProfileId">처음에 고를 프로필. 없으면 첫 줄(프로필이 없으면 새 프로필 초안).</param>
    /// <param name="startNew">새 프로필 초안으로 연다.</param>
    public AiProfileManagerViewModel(
        AiProfileServices services,
        Func<string, Task<bool>> confirm,
        Action<AiSettings, Guid?, Guid?>? applied = null,
        Guid? selectProfileId = null,
        bool startNew = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(confirm);
        _services = services;
        _confirm = confirm;
        _applied = applied;
        _selectNewOnSave = startNew;
        _t = new AiSettingsText(services.Text);
        PresetCards = [.. AiProfilePresets.All.Select(info => new AiPresetCard(info, PresetName(info), CardSubtitle(info)))];
        ReloadItems();
        if (startNew || Items.Count == 0)
        {
            BeginDraft(AiProfilePreset.Custom);
        }
        else
        {
            var first = Items.FirstOrDefault(i => i.Id == selectProfileId) ?? Items[0];
            LoadProfile(_services.Settings.Current.Ai.Profiles.First(p => p.Id == first.Id));
            Ready = RefreshKeyStateAsync(first.Id);
        }
    }

    /// <summary>처음에 고른 프로필의 저장된 키 확인이 끝나면 완료된다(시험이 기다린다).</summary>
    public Task Ready { get; } = Task.CompletedTask;

    public IReadOnlyList<AiPresetCard> PresetCards { get; }

    public ObservableCollection<AiProfileListItem> Items { get; } = [];

    /// <summary>서버가 알려 준 모델 이름(모델명 드롭다운 후보). 서버 없이도 모델명은 직접 입력한다.</summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>키 입력 칸의 지금 글을 읽어 오는 함수(창이 연결). 키를 바인딩에 두지 않기 위한 길이다.</summary>
    public Func<string?>? EnteredKeyProvider { get; set; }

    private IReadOnlyList<AiModelInfo> _detectedModels = [];
    private bool _imagesManual;
    private bool _applyingImages;
    private bool _operationIsTest;

    /// <summary>저장에 성공해 입력 칸의 키를 비워도 된다.</summary>
    public event EventHandler? EnteredKeyConsumed;

    /// <summary>창을 닫아 달라는 요청(취소 단추 · 변경을 버린 뒤).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlainTextWarning), nameof(KeyPlaceholder))]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _modelName = string.Empty;

    [ObservableProperty]
    private string _maxInputTokensText = AiLlmProfile.DefaultMaxInputTokens.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _maxOutputTokensText = AiLlmProfile.DefaultMaxOutputTokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>이미지 입력 지원. null = 모름(체크 해제로 보이고 저장하면 그대로 null).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImagesChecked))]
    private bool? _supportsImages;

    [ObservableProperty]
    private string _imagesNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormatAnthropic), nameof(IsFormatOpenAi), nameof(FormatHint))]
    private AiApiFormat _format = AiApiFormat.Anthropic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatSelectable), nameof(KeyLabelSuffix), nameof(KeyPlaceholder))]
    private AiProfilePreset _preset = AiProfilePreset.Custom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder), nameof(CanClearKey))]
    private bool _hasStoredKey;

    /// <summary>키 저장소를 읽지 못해 저장된 키가 있는지 알 수 없다. 이때도 저장된 키 지우기는 쓸 수 있다(새 키 입력과 함께 막힌 저장을 푸는 길).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearKey))]
    private bool _keyReadFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder))]
    private bool _clearKeyRequested;

    [ObservableProperty]
    private bool _isKeyVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanDelete), nameof(CanProbe), nameof(CanSave))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProbe), nameof(CanSave))]
    private bool _isEditable = true;

    [ObservableProperty]
    private string _invalidReason = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _urlError = string.Empty;

    [ObservableProperty]
    private string _modelError = string.Empty;

    [ObservableProperty]
    private string _inputTokensError = string.Empty;

    [ObservableProperty]
    private string _outputTokensError = string.Empty;

    [ObservableProperty]
    private string _keyError = string.Empty;

    [ObservableProperty]
    private string _generalError = string.Empty;

    [ObservableProperty]
    private string _nameError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultIsSuccess), nameof(ResultIsFailure), nameof(ResultIsInfo))]
    private AiResultKind _resultKind;

    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>모델 새로고침·연결 테스트가 도는 중.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProbe), nameof(CanSave))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProbe), nameof(CanSave))]
    private bool _isSaving;

    public string Title => _t.T("LLM 프로필 관리", "Manage LLM profiles");

    public bool HasSelection => IsEditing;

    /// <summary>확인란 표시: 지원한다고 알 때만 체크. 모름(null)은 체크 해제로 보이고, 사용자가 바꾸기 전에는 저장해도 null 이다.</summary>
    public bool ImagesChecked
    {
        get => SupportsImages == true;
        set => SupportsImages = value;
    }

    public bool FormatSelectable => SafeInfo(Preset)?.FormatSelectable ?? false;

    public bool IsFormatAnthropic
    {
        get => Format == AiApiFormat.Anthropic;
        set
        {
            if (value && FormatSelectable)
            {
                Format = AiApiFormat.Anthropic;
            }
        }
    }

    public bool IsFormatOpenAi
    {
        get => Format == AiApiFormat.OpenAi;
        set
        {
            if (value && FormatSelectable)
            {
                Format = AiApiFormat.OpenAi;
            }
        }
    }

    public string FormatHint => Format == AiApiFormat.OpenAi
        ? _t.T("OpenAI 형식은 앱이 직접 호출합니다.", "OpenAI format is called directly by the app.")
        : _t.T("Anthropic 형식은 이 PC 의 claude 실행 파일로 연결합니다.", "Anthropic format connects through the claude executable on this PC.");

    public string FormatDescription => _t.T("Anthropic 형식은 이 PC 의 claude 실행 파일로 연결합니다. OpenAI 형식은 앱이 직접 호출합니다.",
        "Anthropic format connects through the claude executable on this PC. OpenAI format is called directly by the app.");

    public bool ShowPlainTextWarning => AiProfileUrl.IsPlainTextRemote(BaseUrl);

    public string PlainTextWarningText => _t.T("암호화하지 않은 HTTP 입니다. SQL 과 이미지가 암호화 없이 전달됩니다.",
        "This is plain HTTP. SQL and images are sent without encryption.");

    public string KeyLabelSuffix => SafeInfo(Preset)?.KeyRequirement == AiKeyRequirement.Optional ? _t.T("(선택)", "(optional)") : string.Empty;

    /// <summary>키 입력 칸에 흐리게 보이는 안내. 기존 키가 있으면 「비워 두면 그대로」, 지우기를 눌렀으면 그 사실을 알린다.</summary>
    public string KeyPlaceholder
    {
        get
        {
            if (ClearKeyRequested)
            {
                return _t.T("저장하면 키를 지웁니다", "The key is removed when you save");
            }

            if (HasStoredKey && !StoredKeyMatchesAddress())
            {
                return _t.T("다른 서버라 저장된 키를 보내지 않습니다. 새 키를 입력하거나 키 지우기를 누르세요", "Different server: the stored key is not sent. Enter a new key or clear the stored key");
            }

            if (HasStoredKey)
            {
                return _t.T("저장된 키가 있습니다. 같은 서버에서만 비워 두면 그대로 둡니다", "A key is stored. Leave empty to keep it (same server only)");
            }

            return SafeInfo(Preset)?.KeyRequirement switch
            {
                AiKeyRequirement.NotNeeded => _t.T("이 서버는 키가 필요 없습니다", "This server needs no key"),
                AiKeyRequirement.Optional => _t.T("키가 필요 없으면 비워 두세요", "Leave empty if no key is needed"),
                _ => _t.T("API Key 를 입력하세요", "Enter the API key"),
            };
        }
    }

    public bool HasResult => ResultKind != AiResultKind.None;

    public bool ResultIsSuccess => ResultKind == AiResultKind.Success;

    public bool ResultIsFailure => ResultKind == AiResultKind.Failure;

    public bool ResultIsInfo => ResultKind == AiResultKind.Info;

    /// <summary>서버 조회(모델 새로고침·연결 테스트)를 할 수 있다: 조회 서비스가 있고, 도는 작업이 없으며, 고칠 수 있는 프로필이다.</summary>
    public bool CanProbe => _services.Probe is not null && !IsBusy && !IsSaving && IsEditable && IsEditing;

    /// <summary>「저장된 키 지우기」 단추를 보인다: 저장된 키가 있거나, 있는지 읽지 못했다.</summary>
    public bool CanClearKey => HasStoredKey || KeyReadFailed;

    public bool HasProbe => _services.Probe is not null;

    public string ProbeUnavailableText => _t.T("이 빌드에서는 서버 조회를 쓸 수 없습니다. 모델명을 직접 입력하세요.", "Server queries are not available in this build. Type the model name.");

    public bool CanSave => IsEditable && IsEditing && !IsSaving;

    public bool CanDelete => IsEditing && !_isNew;

    /// <summary>지금 창에서 저장된 프로필 한 개를 편집 중인가(새 프로필이면 false).</summary>
    public bool IsExistingProfile => IsEditing && !_isNew;

    /// <summary>편집 중인 프로필 id(새 프로필이면 저장할 때 쓸 새 id).</summary>
    public Guid EditingId => _editingId;

    // ------------------------------------------------------------------ 목록·불러오기

    private string CardSubtitle(AiProfilePresetInfo info) => info.Preset switch
    {
        AiProfilePreset.Anthropic => _t.T("Claude API (키 필요)", "Claude API (key required)"),
        AiProfilePreset.ZAi => _t.T("GLM 모델 (키 필요)", "GLM models (key required)"),
        AiProfilePreset.Gemini => _t.T("Gemini API (키 필요)", "Gemini API (key required)"),
        AiProfilePreset.OpenAi => _t.T("GPT 모델 (키 필요)", "GPT models (key required)"),
        AiProfilePreset.Ollama => _t.T("로컬 실행 (키 불필요)", "Runs locally (no key)"),
        AiProfilePreset.LmStudio => _t.T("로컬 실행 (키 불필요)", "Runs locally (no key)"),
        _ => _t.T("호환 API 직접 설정", "Configure a compatible API"),
    };

    /// <summary>카드·목록·기본 이름에 보이는 프리셋 이름. Core 의 「직접 입력」 만 한/영을 고른다(제공자 이름은 그대로).</summary>
    private string PresetName(AiProfilePresetInfo info) => info.Preset == AiProfilePreset.Custom ? _t.T(info.DisplayName, "Custom") : info.DisplayName;

    private static AiProfilePresetInfo? SafeInfo(AiProfilePreset preset) => AiProfilePresets.All.FirstOrDefault(p => p.Preset == preset);

    private string ListSubtitle(AiLlmProfile profile)
    {
        if (profile.Preset == AiProfilePreset.Unknown || profile.Format == AiApiFormat.Unknown)
        {
            return _t.T("이 버전에서 쓸 수 없음", "Not usable in this version");
        }

        var format = profile.Format == AiApiFormat.OpenAi ? "OpenAI" : "Anthropic";
        return _t.T($"{PresetName(SafeInfo(profile.Preset)!)} · {format} 형식", $"{PresetName(SafeInfo(profile.Preset)!)} · {format} format");
    }

    private void ReloadItems()
    {
        Items.Clear();
        foreach (var profile in _services.Settings.Current.Ai.Profiles)
        {
            var usable = profile.Preset != AiProfilePreset.Unknown && profile.Format != AiApiFormat.Unknown;
            Items.Add(new AiProfileListItem(profile.Id, profile.Name, ListSubtitle(profile), usable) { IsSelected = profile.Id == _editingId && !_isNew });
        }
    }

    private void MarkSelected()
    {
        foreach (var item in Items)
        {
            item.IsSelected = !_isNew && item.Id == _editingId;
        }

        foreach (var card in PresetCards)
        {
            card.IsSelected = card.Preset == Preset;
        }
    }

    // 편집형 ComboBox 는 목록이 바뀌면 입력한 글을 지울 수 있다: 모델명은 그대로 되돌린다.
    private void ReplaceModels(IEnumerable<string> ids)
    {
        var typed = ModelName;
        var wasLoading = _loading;
        _loading = true; // 목록을 바꾸는 것만으로 변경 표시·검사·취소가 일어나지 않게 한다
        try
        {
            Models.Clear();
            foreach (var id in ids)
            {
                Models.Add(id);
            }

            if (ModelName != typed)
            {
                ModelName = typed;
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void ClearEditorState()
    {
        _attempted = false;
        UrlError = ModelError = InputTokensError = OutputTokensError = KeyError = GeneralError = NameError = string.Empty;
        ResultKind = AiResultKind.None;
        ResultText = string.Empty;
        ClearKeyRequested = false;
        HasStoredKey = false;
        KeyReadFailed = false;
        IsKeyVisible = false;
        ImagesNote = string.Empty;
        _detectedModels = [];
        _imagesManual = false;
        Models.Clear();
        IsDirty = false;
    }

    private void LoadProfile(AiLlmProfile profile)
    {
        CancelOperation();
        _loading = true;
        try
        {
            ClearEditorState();
            _isNew = false;
            _editingId = profile.Id;
            IsEditing = true;
            var usable = profile.Preset != AiProfilePreset.Unknown && profile.Format != AiApiFormat.Unknown;
            IsEditable = usable;
            InvalidReason = usable ? string.Empty : _t.T("이 버전이 모르는 프리셋·API 형식이라 고칠 수 없습니다. 지우기만 할 수 있고, 다른 프로필은 영향을 받지 않습니다.",
                "This version does not recognize this preset or API format, so it cannot be edited. You can only delete it; other profiles are not affected.");
            Preset = profile.Preset;
            Format = profile.Format == AiApiFormat.Unknown ? AiApiFormat.Anthropic : profile.Format;
            Name = profile.Name;
            BaseUrl = profile.BaseUrl;
            ModelName = profile.Model;
            MaxInputTokensText = profile.MaxInputTokens.ToString(CultureInfo.InvariantCulture);
            MaxOutputTokensText = profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
            SupportsImages = profile.SupportsImages;
            MarkSelected();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        OnPropertyChanged(nameof(EditingId));
    }

    private void BeginDraft(AiProfilePreset preset)
    {
        CancelOperation();
        _loading = true;
        try
        {
            ClearEditorState();
            var draft = AiProfilePresets.NewDraft(preset);
            _isNew = true;
            _editingId = draft.Id;
            IsEditing = true;
            IsEditable = true;
            InvalidReason = string.Empty;
            Preset = preset;
            Format = draft.Format;
            Name = PresetName(SafeInfo(preset)!);
            BaseUrl = draft.BaseUrl;
            ModelName = string.Empty;
            MaxInputTokensText = draft.MaxInputTokens.ToString(CultureInfo.InvariantCulture);
            MaxOutputTokensText = draft.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
            SupportsImages = null;
            MarkSelected();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        OnPropertyChanged(nameof(EditingId));
    }

    private async Task RefreshKeyStateAsync(Guid id)
    {
        if (_services.Credentials is not { } store)
        {
            return;
        }

        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
            var stored = bytes is { Length: > 0 };
            if (bytes is not null)
            {
                Array.Clear(bytes);
            }

            if (!_disposed && _editingId == id)
            {
                HasStoredKey = stored;
                KeyReadFailed = false;
            }
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // 키 저장소를 못 읽으면 「저장된 키 있음」 을 알 수 없다. 저장할 때 서버가 바뀌었으면 다시 읽어 막고, 지우기도 쓸 수 있다.
            if (!_disposed && _editingId == id)
            {
                KeyReadFailed = true;
            }
        }
    }

    /// <summary>왼쪽 목록에서 프로필을 고른다. 저장하지 않은 변경이 있으면 버려도 되는지 묻는다.</summary>
    public async Task SelectProfileAsync(Guid id)
    {
        if (IsSaving || (_editingId == id && !_isNew))
        {
            return;
        }

        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var profile = _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return;
        }

        LoadProfile(profile);
        await RefreshKeyStateAsync(id).ConfigureAwait(true);
    }

    /// <summary>「새 프로필」: 직접 입력 초안을 연다. 저장하지 않은 변경이 있으면 버려도 되는지 묻는다.</summary>
    public async Task NewProfileAsync()
    {
        if (IsSaving)
        {
            return;
        }

        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        BeginDraft(AiProfilePreset.Custom);
    }

    /// <summary>저장하지 않은 변경이 있으면 버려도 되는지 묻는다(없으면 true). 창 닫기도 같은 확인을 쓴다.</summary>
    internal async Task<bool> ConfirmDiscardAsync() =>
        !IsDirty || await _confirm(_t.T("저장하지 않은 변경이 있습니다. 버리고 계속할까요?", "There are unsaved changes. Discard them and continue?")).ConfigureAwait(true);

    [RelayCommand]
    private Task SelectItem(AiProfileListItem? item) => item is null ? Task.CompletedTask : SelectProfileAsync(item.Id);

    [RelayCommand]
    private Task NewProfile() => NewProfileAsync();

    // ------------------------------------------------------------------ 프리셋·변경 추적

    /// <summary>
    /// 프리셋 카드를 고른다. 새 프리셋의 <see cref="AiProfilePresets.NewDraft"/> 값(형식·URL)으로 바꾸되, 사용자가 고친 칸은 남긴다:
    /// URL 은 이전 프리셋의 기본값(또는 빈 칸)일 때만 새 기본값으로 바뀌고, 형식은 직접 입력이 아닐 때만 프리셋 값을 따른다.
    /// 모델명·토큰·이름은 지우지 않는다(이름은 이전 프리셋 이름 그대로일 때만 새 이름). 입력 중인 키는 서버(스킴·호스트·포트)가 그대로면 두고,
    /// 다른 서버로 바뀌면 새 서버로 시험·저장되지 않게 지우고 한 줄로 알린다. 서버가 알려 준 모델 후보는 제공자가 바뀌므로 비운다.
    /// </summary>
    public void SelectPreset(AiProfilePreset preset)
    {
        if (IsSaving || !IsEditable || SafeInfo(preset) is null || preset == Preset)
        {
            return;
        }

        var previous = SafeInfo(Preset);
        var draft = AiProfilePresets.NewDraft(preset);
        var next = SafeInfo(preset)!;
        var url = BaseUrl.Trim();
        var urlBefore = url;
        if (url.Length == 0 || (previous is not null && string.Equals(url, previous.DefaultBaseUrl, StringComparison.Ordinal)))
        {
            BaseUrl = draft.BaseUrl;
        }

        if (!next.FormatSelectable)
        {
            Format = draft.Format;
        }

        if (previous is not null && (Name.Trim().Length == 0 || string.Equals(Name.Trim(), PresetName(previous), StringComparison.Ordinal)))
        {
            Name = PresetName(next);
        }

        Preset = preset;
        CancelOperation(); // 앞 제공자에게 보낸 조회의 늦은 결과가 새 프리셋 화면을 바꾸지 못하게 한다
        ReplaceModels([]);
        _detectedModels = [];
        var keyDropped = !SameServer(urlBefore, BaseUrl) && !string.IsNullOrWhiteSpace(EnteredKeyProvider?.Invoke());
        if (keyDropped)
        {
            EnteredKeyConsumed?.Invoke(this, EventArgs.Empty); // 앞 서버에 입력하던 키가 새 서버로 시험·저장되지 않게 칸을 비운다
        }
        if (ImagesNote.Length > 0)
        {
            SupportsImages = null; // 앞 서버가 알려 준 이미지 지원은 새 제공자에게 맞지 않는다
            ImagesNote = string.Empty;
        }

        ResultKind = AiResultKind.None;
        if (keyDropped)
        {
            SetResult(AiResultKind.Info, _t.T("다른 서버로 바뀌어 입력한 API Key 를 지웠습니다. 새 서버의 키를 입력하세요.",
                "The server changed, so the API key you typed was cleared. Enter the key for the new server."));
        }

        MarkSelected();
    }

    [RelayCommand]
    private void ChoosePreset(AiPresetCard? card)
    {
        if (card is not null)
        {
            SelectPreset(card.Preset);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(BaseUrl):
            case nameof(Format):
                if (IsBusy)
                {
                    CancelOperation(); // 바뀌기 전 주소로 보낸 조회는 결과를 버린다
                    ResultKind = AiResultKind.None;
                }

                if (UrlError.Length > 0 && AiProfileUrl.BaseFor(Format, BaseUrl) is not null)
                {
                    UrlError = string.Empty;
                }

                goto case nameof(Name);
            case nameof(ModelName):
                if (IsBusy && _operationIsTest)
                {
                    CancelOperation(); // 바뀌기 전 모델로 보낸 시험 결과는 버린다
                    ResultKind = AiResultKind.None;
                }

                ReapplyDetectedImages();
                if (ModelError.Length > 0 && ModelName.Trim().Length > 0)
                {
                    ModelError = string.Empty;
                }

                goto case nameof(Name);
            case nameof(SupportsImages):
                if (!_applyingImages)
                {
                    _imagesManual = true; // 사용자가 직접 바꾼 값은 목록으로 덮지 않는다(다른 칸을 고친 것은 해당 없음)
                }

                goto case nameof(Name);
            case nameof(Name):
            case nameof(MaxInputTokensText):
            case nameof(MaxOutputTokensText):
            case nameof(Preset):
            case nameof(ClearKeyRequested):
                IsDirty = true;
                if (_attempted)
                {
                    Validate(EnteredKeyProvider?.Invoke());
                }

                break;
        }
    }

    /// <summary>키 칸에 글이 입력됐다(창이 알린다). 키도 저장하지 않은 변경이므로 버림 확인 대상이다.</summary>
    public void NotifyKeyTyped(bool hasText)
    {
        if (hasText && !_loading)
        {
            IsDirty = true;
        }
    }

    [RelayCommand]
    private void ToggleKeyVisible() => IsKeyVisible = !IsKeyVisible;

    /// <summary>저장할 때 저장된 키를 지운다(입력 칸을 비운 것과 다르다: 빈 칸은 변경 없음).</summary>
    [RelayCommand]
    private void ClearStoredKey()
    {
        if (CanClearKey)
        {
            ClearKeyRequested = !ClearKeyRequested;
        }
    }

    [RelayCommand]
    private async Task Cancel()
    {
        if (await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ 입력 검사

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    /// <summary>입력 칸을 검사해 오류 글을 채운다. 키 칸은 저장된 키·지우기 요청까지 함께 본다. 하나라도 틀리면 false.</summary>
    public bool Validate(string? enteredKey)
    {
        _attempted = true;
        NameError = Name.Trim().Length == 0 ? _t.T("프로필 이름을 입력하세요.", "Enter a profile name.") : string.Empty;
        UrlError = AiProfileUrl.BaseFor(Format, BaseUrl) is null
            ? _t.T("http:// 또는 https:// 로 시작하는 올바른 주소를 입력하세요(사용자 정보·쿼리는 쓸 수 없습니다).",
                "Enter a valid address starting with http:// or https:// (user info and query strings are not allowed).")
            : string.Empty;
        ModelError = ModelName.Trim().Length == 0 ? _t.T("모델명을 입력하세요.", "Enter a model name.") : string.Empty;
        InputTokensError = TryPositive(MaxInputTokensText, out _) ? string.Empty : _t.T("1 이상의 정수를 입력하세요.", "Enter an integer of 1 or more.");
        OutputTokensError = TryPositive(MaxOutputTokensText, out _) ? string.Empty : _t.T("1 이상의 정수를 입력하세요.", "Enter an integer of 1 or more.");

        var typed = !string.IsNullOrWhiteSpace(enteredKey);
        var requirement = SafeInfo(Preset)?.KeyRequirement ?? AiKeyRequirement.Optional;
        var willHaveKey = typed || (HasStoredKey && !ClearKeyRequested);
        if (!typed && UrlError.Length == 0 && StoredKeyBlockedByAddress(enteredKey))
        {
            // 저장된 키는 저장된 서버로만 나간다: 서버가 바뀌면 새 키를 넣거나 저장된 키를 지워야 한다(키가 필요 없는 프리셋도 같다).
            KeyError = _t.T("주소가 저장된 프로필과 다른 서버입니다. 저장된 키를 보내지 않으니 새 API Key 를 입력하거나 저장된 키 지우기를 누르세요.",
                "The address points to a different server than the saved profile. The stored key is not sent: enter a new API key or clear the stored key.");
        }
        else if (requirement == AiKeyRequirement.Required && !willHaveKey)
        {
            KeyError = _t.T("이 서비스는 API Key 가 필요합니다.", "This service requires an API key.");
        }
        else if ((typed || ClearKeyRequested) && _services.Credentials is null)
        {
            KeyError = _t.T("키 저장소를 쓸 수 없어 키를 저장하거나 지울 수 없습니다.", "The credential store is unavailable, so the key cannot be saved or removed.");
        }
        else
        {
            KeyError = string.Empty;
        }

        return NameError.Length == 0 && UrlError.Length == 0 && ModelError.Length == 0 && InputTokensError.Length == 0
            && OutputTokensError.Length == 0 && KeyError.Length == 0;
    }

    private AiLlmProfile BuildProfile()
    {
        _ = TryPositive(MaxInputTokensText, out var input);
        _ = TryPositive(MaxOutputTokensText, out var output);
        return new AiLlmProfile
        {
            Id = _editingId,
            Name = Name.Trim(),
            Preset = Preset,
            Format = Format,
            BaseUrl = BaseUrl.Trim(),
            Model = ModelName.Trim(),
            MaxInputTokens = input,
            MaxOutputTokens = output,
            SupportsImages = SupportsImages,
        };
    }

    // ------------------------------------------------------------------ 저장·삭제

    private static bool IsStoreError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException; // Windows DPAPI 가 깨진·옮겨 온 키 파일을 풀 때 던진다

    private string ReadOnlyMessage => _t.T("설정 파일이 이 버전보다 새로워 읽기 전용입니다. 저장하지 않았습니다.",
        "The settings file is newer than this version and is read-only. Nothing was saved.");

    /// <summary>
    /// 입력을 검사하고 프로필을 즉시 기록한다. 순서: 검사 → 읽기 전용 확인 → 키 → 설정. 어느 단계가 실패해도 이유를 <see cref="GeneralError"/> 에 보이고 false 를 돌려주며,
    /// 설정 쓰기가 실패하면 방금 쓴 키를 되돌려 키만 남는 일(고아 키)이 없게 한다.
    /// </summary>
    /// <param name="enteredKey">키 입력 칸의 글. 비어 있으면 저장된 키를 그대로 둔다.</param>
    public async Task<bool> SaveProfileAsync(string? enteredKey)
    {
        if (_disposed || !CanSave)
        {
            return false;
        }

        CancelOperation();
        GeneralError = string.Empty;
        if (!Validate(enteredKey))
        {
            return false;
        }

        if (_services.Settings.IsReadOnly)
        {
            GeneralError = ReadOnlyMessage;
            return false;
        }

        // 키 저장소를 다시 읽는 동안(macOS 키체인 프롬프트가 떠 있을 수 있다)에도 다른 조작이 편집 대상을 바꾸지 못하게 먼저 잠근다.
        IsSaving = true;
        try
        {
            var id = _editingId;
            var address = BaseUrl;
            if (await StoredKeyBlocksSaveAsync(enteredKey).ConfigureAwait(true) || _disposed || id != _editingId || address != BaseUrl)
            {
                return false; // 막혔거나, 기다리는 사이 편집 대상·주소가 달라졌으면 저장하지 않는다
            }

            return await SaveCoreAsync(string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey.Trim()).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// 키를 입력하지 않았고 서버가 저장된 프로필과 달라졌으면, 저장 시점에 키 저장소를 다시 읽는다(창을 열 때의 읽기 실패·읽는 중 저장 때문에
    /// <see cref="HasStoredKey"/> 가 믿을 수 없다). 키가 있거나 읽지 못하면 옛 키가 새 서버로 나갈 수 있으므로 저장을 막는다. 지우기를 누른 경우는 막지 않는다.
    /// </summary>
    private async Task<bool> StoredKeyBlocksSaveAsync(string? enteredKey)
    {
        if (_isNew || !string.IsNullOrWhiteSpace(enteredKey) || ClearKeyRequested || StoredKeyMatchesAddress() || _services.Credentials is not { } store)
        {
            return false;
        }

        var id = _editingId;
        var blocked = false;
        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
            if (bytes is { Length: > 0 })
            {
                blocked = true;
                if (_editingId == id)
                {
                    HasStoredKey = true;
                    KeyReadFailed = false;
                }
            }

            if (bytes is not null)
            {
                Array.Clear(bytes);
            }
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            blocked = true; // 읽지 못하면 키가 있는지 알 수 없다: 안전한 쪽으로 막는다
            if (_editingId == id)
            {
                KeyReadFailed = true; // 지우기 단추를 쓸 수 있게 한다
            }
        }

        if (blocked && !_disposed)
        {
            KeyError = _t.T("주소가 저장된 프로필과 다른 서버입니다. 저장된 키를 보내지 않으니 새 API Key 를 입력하거나 저장된 키 지우기를 누르세요(키 저장소를 읽지 못했어도 지우기를 쓸 수 있습니다).",
                "The address points to a different server than the saved profile. The stored key is not sent: enter a new API key or clear the stored key (clearing also works when the key store could not be read).");
        }

        return blocked;
    }

    private async Task<bool> SaveCoreAsync(string? newKey)
    {
        var profile = BuildProfile();
        var credentialName = AiProfileCredentials.KeyFor(profile.Id);
        var clear = newKey is null && ClearKeyRequested;
        var store = _services.Credentials;
        byte[]? previousKey = null;
        var keyWritten = false;

        if (clear && store is not null)
        {
            // 서버가 바뀐 프로필의 옛 키가 남지 않게 설정을 쓰기 전에 지운다. 지우지 못하면 저장하지 않는다.
            try
            {
                try
                {
                    previousKey = await store.LoadAsync(credentialName).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    previousKey = null;
                }

                await store.DeleteAsync(credentialName).ConfigureAwait(true);
                keyWritten = true;
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                GeneralError = _t.T("저장된 키를 지우지 못했습니다. 프로필은 저장하지 않았습니다: ", "Could not remove the stored key. The profile was not saved: ") + ex.Message;
                return false;
            }
        }

        if (newKey is not null && store is not null)
        {
            var secret = Encoding.UTF8.GetBytes(newKey);
            try
            {
                try
                {
                    previousKey = await store.LoadAsync(credentialName).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    previousKey = null; // 읽을 수 없는 옛 키는 복원할 수 없다. 새 키를 입력해 고치려는 저장은 막지 않는다
                }

                await store.SaveAsync(credentialName, secret).ConfigureAwait(true);
                keyWritten = true;
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                GeneralError = _t.T("API 키를 저장하지 못했습니다. 프로필은 저장하지 않았습니다: ", "Could not save the API key. The profile was not saved: ") + ex.Message;
                return false;
            }
            finally
            {
                Array.Clear(secret);
            }
        }

        var before = _services.Settings.Current.Ai;
        try
        {
            await _services.Settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = Upsert(s.Ai.Profiles, profile) } }).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            var (rollbackNote, keyLost) = await RollbackAsync(keyWritten, previousKey, credentialName, before, keyCleared: clear).ConfigureAwait(true);
            if (keyLost)
            {
                HasStoredKey = false; // 지운 키를 되돌리지 못했다: 화면도 키가 없다고 맞춘다
                KeyReadFailed = false;
                ClearKeyRequested = false;
            }

            GeneralError = _t.T("설정 파일에 저장하지 못했습니다: ", "Could not write the settings file: ") + ex.Message + rollbackNote;
            if (previousKey is not null)
            {
                Array.Clear(previousKey);
            }

            return false;
        }

        if (previousKey is not null)
        {
            Array.Clear(previousKey);
        }

        var wasNew = _isNew;
        _isNew = false;
        _attempted = false;
        HasStoredKey = newKey is not null || (HasStoredKey && !clear);
        ClearKeyRequested = false;
        IsDirty = false; // 위 대입이 변경 표시를 켜므로 마지막에 끈다
        var latest = _services.Settings.Current.Ai;
        PublishChanged(before, latest);
        _applied?.Invoke(latest, null, wasNew && _selectNewOnSave ? profile.Id : null);
        ReloadItems();
        MarkSelected();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        EnteredKeyConsumed?.Invoke(this, EventArgs.Empty);
        GeneralError = string.Empty;
        SetResult(AiResultKind.Success, _t.T("프로필을 저장했습니다.", "Profile saved."));
        return true;
    }

    private async Task<(string Note, bool KeyLost)> RollbackAsync(bool keyWritten, byte[]? previousKey, string credentialName, AiSettings before, bool keyCleared = false)
    {
        var note = string.Empty;
        var keyLost = false;
        if (keyWritten && _services.Credentials is { } store)
        {
            try
            {
                if (previousKey is not null)
                {
                    await store.SaveAsync(credentialName, previousKey).ConfigureAwait(true);
                }
                else
                {
                    await store.DeleteAsync(credentialName).ConfigureAwait(true);
                    if (keyCleared)
                    {
                        // 지우기 전에 옛 키를 읽지 못했으므로 되돌릴 값이 없다: 키는 사라졌다.
                        keyLost = true;
                        note = " " + _t.T("또한 지운 저장된 키는 읽을 수 없어 되돌리지 못했습니다. 키가 사라졌으니 API Key 를 다시 입력하세요.",
                            "Also, the stored key that was removed could not be read beforehand and cannot be restored. The key is gone: enter the API key again.");
                    }
                }
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                if (keyCleared)
                {
                    keyLost = true;
                    note = " " + _t.T("또한 지운 저장된 키를 되돌리지 못해 키가 사라졌습니다. API Key 를 다시 입력하세요: ",
                        "Also, the stored key that was removed could not be restored, so the key is gone. Enter the API key again: ") + ex.Message;
                }
                else
                {
                    note = " " + _t.T("또한 방금 저장한 키를 되돌리지 못했습니다(저장소에 새 키가 남았을 수 있습니다): ", "Also, the key that was just saved could not be undone (the new key may remain in the store): ") + ex.Message;
                }
            }
        }

        // 설정 서비스는 저장소에 쓰기 전에 메모리 값을 바꾼다: 쓰기 실패 뒤에도 화면·실행이 쓰지 않은 프로필을 보지 않도록 메모리 값을 되돌린다.
        try
        {
            await _services.Settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = before.Profiles, ActiveProfileId = before.ActiveProfileId, SystemCliId = before.SystemCliId } }).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // 같은 저장소가 다시 실패해도 메모리 값은 이미 되돌려졌다.
        }

        return (note, keyLost);
    }

    private static List<AiLlmProfile> Upsert(IReadOnlyList<AiLlmProfile> list, AiLlmProfile profile)
    {
        var next = list.ToList();
        var index = next.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            next[index] = profile;
        }
        else
        {
            next.Add(profile);
        }

        return next;
    }

    /// <summary>
    /// 편집 중인 저장된 프로필을 지운다. 확인을 거친 뒤 설정 → 키 순으로 지운다. 활성 프로필이면 <c>ActiveProfileId</c> 를 비워 Claude Code 로 되돌린다(설정 쪽 페이지가 안내를 보인다).
    /// 설정 파일이 읽기 전용이면 지우지 않는다. 키 삭제가 실패해도 프로필은 이미 지워졌으므로 안내만 하고 true 를 돌려준다.
    /// </summary>
    public async Task<bool> DeleteProfileAsync()
    {
        if (_disposed || !CanDelete || IsSaving)
        {
            return false;
        }

        CancelOperation();
        var id = _editingId;
        var name = Name.Trim().Length > 0 ? Name.Trim() : _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? string.Empty;
        var inUse = _services.Settings.Current.Ai.ActiveProfileId == id;
        var question = _t.T($"프로필 「{name}」 과(와) 저장된 키를 지웁니다. 계속할까요?", $"Delete the profile \"{name}\" and its stored key?");
        if (inUse)
        {
            question += " " + _t.T("이 프로필은 지금 사용 중이라 지우면 Claude Code 로 되돌아갑니다.", "This profile is in use, so Claude Code is selected again after deleting it.");
        }

        // 확인을 기다리는 동안 창이 닫혔으면(Dispose) 지우지 않는다.
        if (!await _confirm(question).ConfigureAwait(true) || _disposed)
        {
            return false;
        }

        GeneralError = string.Empty;
        if (_services.Settings.IsReadOnly)
        {
            GeneralError = ReadOnlyMessage;
            return false;
        }

        IsSaving = true;
        try
        {
            var before = _services.Settings.Current.Ai;
            try
            {
                await _services.Settings.UpdateAsync(s => s with
                {
                    Ai = s.Ai with
                    {
                        Profiles = [.. s.Ai.Profiles.Where(p => p.Id != id)],
                        ActiveProfileId = s.Ai.ActiveProfileId == id ? null : s.Ai.ActiveProfileId,
                        SystemCliId = s.Ai.ActiveProfileId == id ? null : s.Ai.SystemCliId, // 되돌아가는 곳은 Claude Code 이므로 낡은 시스템 CLI 값도 비운다
                    },
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                _ = await RollbackAsync(false, null, string.Empty, before).ConfigureAwait(true);
                GeneralError = _t.T("설정 파일에 저장하지 못했습니다: ", "Could not write the settings file: ") + ex.Message;
                return false;
            }

            string? note = null;
            if (_services.Credentials is { } store)
            {
                try
                {
                    await store.DeleteAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    note = _t.T("프로필은 지웠지만 저장된 키를 지우지 못했습니다: ", "The profile was deleted, but the stored key could not be removed: ") + ex.Message;
                }
            }

            var latest = _services.Settings.Current.Ai;
            PublishChanged(before, latest);
            _applied?.Invoke(latest, id, null);
            ReloadItems();
            IsDirty = false;
            if (Items.FirstOrDefault(i => i.IsUsable) is { } next)
            {
                LoadProfile(latest.Profiles.First(p => p.Id == next.Id));
                await RefreshKeyStateAsync(next.Id).ConfigureAwait(true);
            }
            else
            {
                BeginDraft(AiProfilePreset.Custom);
            }

            GeneralError = note ?? string.Empty;
            SetResult(note is null ? AiResultKind.Success : AiResultKind.Info, note ?? _t.T("프로필을 지웠습니다.", "Profile deleted."));
            return true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void PublishChanged(AiSettings before, AiSettings after)
    {
        if (before != after)
        {
            _services.Bus?.Publish(new SettingsChanged("ai"));
        }
    }

    [RelayCommand]
    private async Task Save() => await SaveProfileAsync(EnteredKeyProvider?.Invoke()).ConfigureAwait(true);

    [RelayCommand]
    private async Task Delete() => await DeleteProfileAsync().ConfigureAwait(true);

    // ------------------------------------------------------------------ 서버 조회 (모델 새로고침 · 연결 테스트)

    private void SetResult(AiResultKind kind, string text)
    {
        ResultText = text;
        ResultKind = kind;
    }

    private CancellationToken BeginOperation()
    {
        CancelOperation();
        _operation = new CancellationTokenSource();
        _operationIsTest = false;
        IsBusy = true;
        return _operation.Token;
    }

    /// <summary>도는 서버 조회를 멈추고 그 결과가 화면을 바꾸지 못하게 한다(프로필 전환·저장·창 닫기).</summary>
    public void CancelOperation()
    {
        _version++;
        var running = _operation;
        _operation = null;
        if (running is not null)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 이미 끝난 작업이다.
            }

            running.Dispose();
        }

        if (IsBusy)
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopProbe()
    {
        if (IsBusy)
        {
            CancelOperation();
            SetResult(AiResultKind.Info, _t.T("작업을 멈췄습니다.", "Stopped."));
        }
    }

    // 저장된 키는 저장된 프로필의 서버(스킴·호스트·포트)로만 쓴다: 주소를 고친 채 키 칸을 비워 두고 시험하면 옛 키가 새 서버로 나가기 때문이다.
    private bool StoredKeyMatchesAddress()
    {
        var saved = _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == _editingId);
        return saved is not null && SameServer(saved.BaseUrl, BaseUrl);
    }

    private static bool SameServer(string? a, string? b) =>
        Uri.TryCreate(a?.Trim(), UriKind.Absolute, out var left)
        && Uri.TryCreate(b?.Trim(), UriKind.Absolute, out var right)
        && left.GetLeftPart(UriPartial.Authority).Equals(right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>키 칸이 비어 있고 저장된 키가 있는데 주소가 다른 서버로 바뀌었는가(이때 저장된 키로 시험하지 않는다).</summary>
    private bool StoredKeyBlockedByAddress(string? entered) =>
        string.IsNullOrWhiteSpace(entered) && HasStoredKey && !ClearKeyRequested && !StoredKeyMatchesAddress();

    private async Task<string?> ResolveKeyAsync(string? entered)
    {
        if (!string.IsNullOrWhiteSpace(entered))
        {
            return entered.Trim();
        }

        if (!HasStoredKey || ClearKeyRequested || _services.Credentials is not { } store || !StoredKeyMatchesAddress())
        {
            return null;
        }

        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(_editingId)).ConfigureAwait(true);
            if (bytes is null)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return null;
        }
    }

    private bool CheckProbeInputs(bool needModel)
    {
        UrlError = AiProfileUrl.BaseFor(Format, BaseUrl) is null
            ? _t.T("http:// 또는 https:// 로 시작하는 올바른 주소를 입력하세요(사용자 정보·쿼리는 쓸 수 없습니다).",
                "Enter a valid address starting with http:// or https:// (user info and query strings are not allowed).")
            : string.Empty;
        ModelError = needModel && ModelName.Trim().Length == 0 ? _t.T("모델명을 입력하세요.", "Enter a model name.") : string.Empty;
        return UrlError.Length == 0 && ModelError.Length == 0;
    }

    /// <summary>서버의 모델 목록을 읽어 모델 후보와 이미지 지원 확인란을 채운다. 저장 전 입력으로도 할 수 있다. 사용자가 확인란을 바꿀 수 있다.</summary>
    public async Task RefreshModelsAsync(string? enteredKey)
    {
        if (_services.Probe is not { } probe || !CanProbe || !CheckProbeInputs(needModel: false))
        {
            return;
        }

        if (StoredKeyBlockedByAddress(enteredKey))
        {
            SetResult(AiResultKind.Failure, _t.T("주소가 저장된 프로필과 다른 서버로 바뀌어 저장된 키를 보내지 않습니다. API Key 를 다시 입력하세요.",
                "The address now points to a different server than the saved profile, so the stored key is not sent. Enter the API key again."));
            return;
        }

        var token = BeginOperation();
        var version = _version;
        var key = await ResolveKeyAsync(enteredKey).ConfigureAwait(true);
        if (_disposed || version != _version)
        {
            return;
        }

        SetResult(AiResultKind.Info, _t.T("모델 목록을 불러오는 중…", "Loading the model list…"));
        try
        {
            var result = await probe.ListModelsAsync(new AiProfileProbeRequest(BuildProfile(), key), token).ConfigureAwait(true);
            if (_disposed || version != _version)
            {
                return;
            }

            if (!result.Success)
            {
                SetResult(AiResultKind.Failure, _t.T("모델 목록을 불러오지 못했습니다: ", "Could not load the model list: ") + (result.Message ?? string.Empty));
                return;
            }

            ReplaceModels(result.Models.Select(m => m.Id));

            if (ModelName.Trim().Length == 0 && result.Models.Count > 0)
            {
                ModelName = result.Models[0].Id;
            }

            ApplyDetectedImages(result.Models);
            SetResult(AiResultKind.Success, _t.T($"모델 {result.Models.Count}개를 불러왔습니다.", $"Loaded {result.Models.Count} model(s)."));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 멈춤·창 닫기·프로필 전환이다(취소 뒤에 다른 예외로 끝나도 같다). 안내는 멈춘 쪽이 이미 남겼다.
        }
        catch (OperationCanceledException) when (!_disposed && version == _version)
        {
            // 구현 자체의 시간 초과: 취소 요청이 아니므로 실패로 보인다.
            SetResult(AiResultKind.Failure, _t.T("서버가 제때 응답하지 않았습니다.", "The server did not respond in time."));
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyDetectedImages(IReadOnlyList<AiModelInfo> models)
    {
        _detectedModels = models;
        _imagesManual = false; // 새 목록을 받으면 다시 서버 값을 따른다
        ReapplyDetectedImages();
    }

    // 받은 목록에서 지금 모델의 이미지 지원을 다시 찾는다. 사용자가 확인란을 직접 바꿨으면 건드리지 않는다.
    private void ReapplyDetectedImages()
    {
        if (_detectedModels.Count == 0 || _imagesManual)
        {
            return;
        }

        var chosen = _detectedModels.FirstOrDefault(m => string.Equals(m.Id, ModelName.Trim(), StringComparison.Ordinal));
        _applyingImages = true;
        try
        {
            if (chosen?.SupportsImages is { } supports)
            {
                SupportsImages = supports;
                ImagesNote = _t.T("서버 응답에서 자동으로 확인함", "Detected from the server response");
            }
            else if (ImagesNote.Length > 0)
            {
                SupportsImages = null; // 앞 모델에서 자동으로 채운 값이 이 모델에는 맞지 않는다
                ImagesNote = string.Empty;
            }
        }
        finally
        {
            _applyingImages = false;
        }
    }

    /// <summary>고른 모델로 작은 요청을 한 번 보내 성공·실패·걸린 시간을 보인다. 저장 전 입력으로도 할 수 있고 도중에 멈출 수 있다.</summary>
    public async Task TestConnectionAsync(string? enteredKey)
    {
        if (_services.Probe is not { } probe || !CanProbe || !CheckProbeInputs(needModel: true))
        {
            return;
        }

        if (StoredKeyBlockedByAddress(enteredKey))
        {
            SetResult(AiResultKind.Failure, _t.T("주소가 저장된 프로필과 다른 서버로 바뀌어 저장된 키를 보내지 않습니다. API Key 를 다시 입력하세요.",
                "The address now points to a different server than the saved profile, so the stored key is not sent. Enter the API key again."));
            return;
        }

        var token = BeginOperation();
        _operationIsTest = true;
        var version = _version;
        var key = await ResolveKeyAsync(enteredKey).ConfigureAwait(true);
        if (_disposed || version != _version)
        {
            return;
        }

        SetResult(AiResultKind.Info, _t.T("연결을 시험하는 중…", "Testing the connection…"));
        try
        {
            var result = await probe.TestConnectionAsync(new AiProfileProbeRequest(BuildProfile(), key), token).ConfigureAwait(true);
            if (_disposed || version != _version)
            {
                return;
            }

            var seconds = result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            var detail = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : " · " + result.Message;
            SetResult(
                result.Success ? AiResultKind.Success : AiResultKind.Failure,
                result.Success
                    ? _t.T($"연결 성공 · {seconds} 초{detail}", $"Connected · {seconds} s{detail}")
                    : _t.T($"연결 실패 · {seconds} 초{detail}", $"Connection failed · {seconds} s{detail}"));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 멈춤·창 닫기·프로필 전환이다(취소 뒤에 다른 예외로 끝나도 같다).
        }
        catch (OperationCanceledException) when (!_disposed && version == _version)
        {
            SetResult(AiResultKind.Failure, _t.T("서버가 제때 응답하지 않았습니다.", "The server did not respond in time."));
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshModels() => RefreshModelsAsync(EnteredKeyProvider?.Invoke());

    [RelayCommand]
    private Task TestConnection() => TestConnectionAsync(EnteredKeyProvider?.Invoke());

    /// <summary>창을 닫을 때: 도는 조회를 멈추고, 늦게 도착한 결과가 닫힌 창을 바꾸지 않게 한다.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelOperation();
    }
}
