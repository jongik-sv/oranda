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
    [NotifyPropertyChangedFor(nameof(ShowPlainTextWarning), nameof(KeyPlaceholder), nameof(ShowLocalAiPlainTextWarning))]
    private string _baseUrl = string.Empty;

    /// <summary>「로컬 AI」 지정(D-045): 값을 가리지 않고 이 서버로 보낸다. 기본 꺼짐이고, 주소를 고치면 풀린다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalAiPlainTextWarning))]
    private bool _localAi;

    // 「로컬 AI」 로 지정할 때의 주소. 이 주소에서 벗어나면 지정을 푼다(글자 앞뒤 공백은 같은 주소다).
    private string? _localAiUrl;

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

    public string LocalAiLabel => _t.T("로컬 AI (값을 가리지 않고 보냄)", "Local AI (send values unmasked)");

    public string LocalAiHint => _t.T(
        "켜면 운영 접속을 포함해 SQL 의 값, 주석, 오류 글, 히스토그램 끝값을 가리지 않고 이 서버로 보냅니다. 비밀번호, 주민번호, 카드, 계좌, 주소, 인증값 열의 히스토그램 끝값은 계속 가립니다(열 이름으로 판단합니다). SQL 문장에 직접 쓴 값은 열과 관계없이 그대로 갑니다. 직접 관리하는 서버에만 켜세요. 서버 주소를 고치면 꺼집니다.",
        "When on, SQL values, comments, error text and histogram endpoints go to this server unmasked, including on production connections. Histogram endpoints of password, resident number, card, account, address and credential columns stay masked (judged by column name). Values written in SQL text go as they are, whatever the column. Turn it on only for a server you manage. Changing the server address turns it off.");

    public bool ShowLocalAiPlainTextWarning => LocalAi && ShowPlainTextWarning;

    private string LocalAiLapsedText => _t.T("주소가 바뀌어 「로컬 AI」 지정을 풀었습니다. 이 서버가 맞으면 다시 켜세요.",
        "The address changed, so the Local AI setting was turned off. Turn it on again if this server is right.");

    public string LocalAiPlainTextWarningText => _t.T("이 서버는 암호화하지 않은 HTTP 라서 가리지 않은 값이 암호화 없이 전달됩니다.",
        "This server uses plain HTTP, so unmasked values are sent without encryption.");

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
}
