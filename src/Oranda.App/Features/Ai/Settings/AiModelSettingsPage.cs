using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>프로필 드롭다운 한 줄의 종류.</summary>
public enum AiChoiceKind
{
    /// <summary>묶음 머리 줄(시스템 CLI · 내 프로필). 고를 수 없다.</summary>
    Header,

    /// <summary>시스템 CLI 의 Claude Code.</summary>
    ClaudeCode,

    /// <summary>준비 중인 시스템 CLI(Antigravity CLI · Codex CLI). 비활성.</summary>
    DisabledCli,

    /// <summary>저장된 프로필. 이 빌드가 모르는 프리셋·형식이면 <see cref="AiProfileChoice.Reason"/> 이 있고 고를 수 없다.</summary>
    Profile,

    /// <summary>설정이 가리키는 프로필이 지워졌다. 이유를 보이는 현재 선택 줄이다(고를 수 없음).</summary>
    MissingProfile,

    /// <summary><c>+ 새 프로필 만들기…</c>. 고르면 관리 창을 열 뿐 선택은 바뀌지 않는다.</summary>
    NewProfile,
}

/// <summary>프로필 드롭다운 한 줄.</summary>
/// <param name="Detail">오른쪽에 흐리게 보이는 글(모델명 · 「준비 중」).</param>
/// <param name="Reason">고를 수 없는 이유(잘못된·지워진 프로필).</param>
public sealed record AiProfileChoice(AiChoiceKind Kind, string Title, string? Detail = null, Guid? ProfileId = null, bool IsCurrent = false, string? Reason = null)
{
    public bool IsHeader => Kind == AiChoiceKind.Header;

    public bool IsNewProfile => Kind == AiChoiceKind.NewProfile;

    /// <summary>머리 줄이 아니고 일반 줄(머리·새 프로필 줄이 아님).</summary>
    public bool IsRow => Kind is not (AiChoiceKind.Header or AiChoiceKind.NewProfile);

    /// <summary>고를 수 있는 줄.</summary>
    public bool Selectable => Kind is AiChoiceKind.ClaudeCode or AiChoiceKind.NewProfile || (Kind == AiChoiceKind.Profile && Reason is null);

    /// <summary>들여쓰기 없이 체크 표시만 보이는 줄(현재 선택).</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail ?? Reason);

    public string DetailText => Reason ?? Detail ?? string.Empty;
}

/// <summary>프로필 관리 창을 열어 달라는 요청.</summary>
/// <param name="ProfileId">처음에 고를 프로필. null 이면 첫 줄.</param>
/// <param name="StartNew">새 프로필 초안으로 연다.</param>
public sealed class ProfileManagerRequestedEventArgs(Guid? profileId, bool startNew) : EventArgs
{
    public Guid? ProfileId { get; } = profileId;

    public bool StartNew { get; } = startNew;
}

/// <summary>
/// 설정 「AI 모델」 쪽: 「선택 안 함 / 직접 선택」 카드, 「직접 선택」 이면 프로필 드롭다운(시스템 CLI · 내 프로필)과 아래 칸. 값은 설정 파일의 <c>ai</c> 섹션(<see cref="AiSettings"/>)이다.
/// 저장 규칙(설계 §6.3): <c>Driver=None</c> 은 끔, <c>Driver=ClaudeCode</c> 에 <c>ActiveProfileId</c> 가 null 이면 Claude Code, 값이 있으면 그 프로필이다.
/// Claude Code 이면 CLI 모델·effort 를, 프로필이면 모델·주소·형식·이미지를 읽기 전용으로 보인다. 리터럴 가림 확인란은 항상 둔다.
/// 프로필 목록 편집은 별도 관리 창이 즉시 기록하고, 이 쪽은 <see cref="ApplyStoreChange"/> 로 목록만 새로 받는다
/// (설정 창 확인이 옛 사본으로 프로필을 덮지 않도록 <see cref="ToSettings(AiSettings)"/> 가 현재 목록을 쓴다).
/// 이 쪽이 건드리지 않는 값(실행 파일 경로)은 읽은 그대로 <see cref="ToSettings()"/> 에 실려 나간다.
/// </summary>
public sealed partial class AiModelSettingsPage : ObservableObject
{
    public static readonly IReadOnlyList<string> KnownModels = ["opus", "sonnet", "haiku"];

    /// <summary>effort 를 넘기지 않는(CLI 기본) 줄의 글.</summary>
    public const string DefaultEffortText = "기본 (Claude Code)";

    private readonly AiSettingsText _t;
    private AiSettings _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelEnabled), nameof(IsNone), nameof(IsClaudeCode), nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(ShowSelection), nameof(ShowCliSettings), nameof(ShowProfileInfo), nameof(ShowProblem), nameof(ProblemText))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle), nameof(ShowPlainTextWarning), nameof(Choices))]
    private AiDriverKind _driver;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _model;

    [ObservableProperty]
    private bool _maskLiterals;

    /// <summary>운영 벤치마크 제한 시간(초) 입력 글. 올바르지 않으면 <see cref="ToSettings(AiSettings)"/> 가 설정의 기존 값을 그대로 둔다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BenchmarkTimeoutValid), nameof(ShowBenchmarkTimeoutError), nameof(ShowBenchmarkTimeoutWarning))]
    private string _benchmarkTimeoutText;

    /// <summary>입력한 글에서 읽은 제한 시간(초). 정수가 아니거나 범위(0 또는 5~3600) 밖이면 null.</summary>
    public int? ParsedBenchmarkTimeout =>
        int.TryParse(BenchmarkTimeoutText?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && AiSettings.IsValidBenchmarkTimeout(seconds) ? seconds : null;

    public bool BenchmarkTimeoutValid => ParsedBenchmarkTimeout is not null;

    public bool ShowBenchmarkTimeoutError => !BenchmarkTimeoutValid;

    /// <summary>0(제한 없음)을 입력했다: 운영 DB 에서 사용자가 중지할 때까지 도는 경고를 보인다.</summary>
    public bool ShowBenchmarkTimeoutWarning => ParsedBenchmarkTimeout == 0;

    public string BenchmarkTimeoutErrorText => _t.T("5~3600초 또는 0(제한 없음)으로 입력하세요.", "Enter 5-3600 seconds, or 0 for no limit.");

    public string BenchmarkTimeoutWarningText => _t.T("사용자가 중지할 때까지 운영 DB 에서 실행합니다", "Runs on the production database until you stop it");

    /// <summary>고른 effort 줄: <see cref="DefaultEffortText"/> 이면 설정에 null(CLI 기본).</summary>
    [ObservableProperty]
    private string _effort;

    /// <summary>쓰는 프로필 id. null 이면 Claude Code. 목록에 없는 값(지워진 프로필·읽을 수 없는 값)도 그대로 들고 있다가 저장한다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(ActiveKind), nameof(ActiveProfile))]
    [NotifyPropertyChangedFor(nameof(ShowSelection), nameof(ShowCliSettings), nameof(ShowProfileInfo), nameof(ShowProblem), nameof(ProblemText))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle), nameof(ShowPlainTextWarning), nameof(Choices), nameof(ProfileModel), nameof(ProfileUrl), nameof(ProfileFormatText), nameof(ProfileImagesText))]
    private Guid? _activeProfileId;

    /// <summary>저장된 프로필 목록(설정 파일의 사본). 관리 창이 기록한 뒤 <see cref="ApplyStoreChange"/> 로 갱신된다.</summary>
    private IReadOnlyList<AiLlmProfile> _profiles;

    /// <summary>드롭다운 아래에 보이는 한 줄 안내(활성 프로필을 지워 Claude Code 로 되돌렸을 때 등). 비어 있으면 숨김.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    [ObservableProperty]
    private bool _isDropdownOpen;

    public AiModelSettingsPage(AiSettings? settings = null, AiProfileServices? services = null)
    {
        _source = settings ?? new AiSettings();
        Services = services;
        _t = new AiSettingsText(services?.Text);
        _driver = _source.Driver;
        _activeProfileId = _source.ActiveProfileId;
        _profiles = _source.Profiles;
        _maskLiterals = _source.MaskLiterals;
        _benchmarkTimeoutText = _source.BenchmarkTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model = string.IsNullOrWhiteSpace(_source.Model) ? KnownModels[0] : _source.Model;
        var effort = _source.Effort?.Trim().ToLowerInvariant();
        _effort = effort is not null && AiSettings.EffortLevels.Contains(effort) ? effort : DefaultEffortText;
    }

    /// <summary>프로필 관리에 쓰는 서비스. null 이면 「프로필 관리」 단추가 비활성이다.</summary>
    public AiProfileServices? Services { get; }

    /// <summary>프로필 관리 창을 열어 달라는 요청(「프로필 관리」 단추 · 「새 프로필 만들기…」 줄). 창은 뷰가 연다.</summary>
    public event EventHandler<ProfileManagerRequestedEventArgs>? ProfileManagerRequested;

    /// <summary>관리 창이 프로필 목록을 기록해 설정에 반영됐다(설정 창이 미확정 변경 비교 기준을 맞추는 데 쓴다).</summary>
    public event EventHandler? ProfilesPersisted;

    /// <summary>고를 수 있는 모델 이름(화면 바인딩용 사본).</summary>
    public IReadOnlyList<string> Models { get; } = KnownModels;

    /// <summary>고를 수 있는 effort 줄(CLI 기본 + low~max).</summary>
    public IReadOnlyList<string> Efforts { get; } = [DefaultEffortText, .. AiSettings.EffortLevels];

    /// <summary>저장된 프로필 목록(읽기 전용 사본).</summary>
    public IReadOnlyList<AiLlmProfile> Profiles => _profiles;

    public bool HasNotice => Notice.Length > 0;

    public bool ModelEnabled => Driver == AiDriverKind.ClaudeCode;

    public bool IsNone
    {
        get => Driver == AiDriverKind.None;
        set
        {
            if (value)
            {
                Driver = AiDriverKind.None;
            }
        }
    }

    /// <summary>「직접 선택」 카드: Claude Code 든 프로필이든 AI 를 쓴다(<c>Driver == ClaudeCode</c>). 어느 쪽인지는 <see cref="ActiveKind"/>.</summary>
    public bool IsClaudeCode
    {
        get => Driver == AiDriverKind.ClaudeCode;
        set
        {
            if (value)
            {
                Driver = AiDriverKind.ClaudeCode;
            }
        }
    }

    /// <summary>설정이 지금 가리키는 실행 대상(끔 · Claude Code · 프로필 · 지워진 프로필 · 잘못된 프로필).</summary>
    public AiActiveKind ActiveKind => Active.Kind;

    /// <summary>고른 프로필(<see cref="AiActiveKind.Profile"/> · <see cref="AiActiveKind.ProfileInvalid"/> 일 때만).</summary>
    public AiLlmProfile? ActiveProfile => Active.Profile;

    private AiActiveProfile Active => AiActiveProfile.Find(new AiSettings { Driver = Driver, ActiveProfileId = ActiveProfileId, Profiles = _profiles });

    public bool ShowSelection => Driver == AiDriverKind.ClaudeCode;

    public bool ShowCliSettings => ShowSelection && ActiveKind == AiActiveKind.ClaudeCode;

    public bool ShowProfileInfo => ShowSelection && ActiveKind == AiActiveKind.Profile;

    public bool ShowProblem => ShowSelection && ActiveKind is AiActiveKind.ProfileMissing or AiActiveKind.ProfileInvalid;

    /// <summary>지워진 프로필·잘못된 프로필이면 그 이유(말없이 Claude Code 로 되돌리지 않고 선택지에 보인다).</summary>
    public string ProblemText => ActiveKind switch
    {
        AiActiveKind.ProfileMissing => _t.T("선택한 프로필을 찾을 수 없습니다. 지워졌거나 설정 파일이 바뀌었습니다. 다른 프로필을 고르거나 Claude Code 를 고르세요. 이대로 두면 AI 를 실행할 때 오류가 납니다.",
            "The selected profile was not found. It may have been deleted or the settings file changed. Pick another profile or Claude Code. If left as is, AI runs fail with an error."),
        AiActiveKind.ProfileInvalid => _t.T("이 프로필의 프리셋·API 형식을 이 버전이 모릅니다(다음 버전이 만든 프로필일 수 있습니다). 다른 프로필을 고르세요. 지우지 않으면 프로필은 그대로 남습니다.",
            "This version does not recognize this profile's preset or API format (a newer version may have created it). Pick another profile. The profile is kept unless you delete it."),
        _ => string.Empty,
    };

    /// <summary>드롭다운 단추에 보이는 이름.</summary>
    public string SelectedTitle => ActiveKind switch
    {
        AiActiveKind.ClaudeCode => "Claude Code",
        AiActiveKind.Profile or AiActiveKind.ProfileInvalid => ActiveProfile!.Name,
        AiActiveKind.ProfileMissing => _t.T("삭제된 프로필", "Deleted profile"),
        _ => string.Empty,
    };

    public string ProfileModel => ActiveProfile?.Model ?? string.Empty;

    public string ProfileUrl => ActiveProfile?.BaseUrl ?? string.Empty;

    public string ProfileFormatText => ActiveProfile?.Format switch
    {
        AiApiFormat.Anthropic => _t.T("Anthropic (claude 실행 파일 사용)", "Anthropic (uses the claude executable)"),
        AiApiFormat.OpenAi => _t.T("OpenAI (앱이 직접 호출)", "OpenAI (called directly by the app)"),
        _ => string.Empty,
    };

    public string ProfileImagesText => ActiveProfile?.SupportsImages switch
    {
        true => _t.T("지원", "Supported"),
        false => _t.T("지원 안 함", "Not supported"),
        _ => _t.T("모름 (보내 보고 서버 오류를 확인)", "Unknown (sent as is; server errors are shown)"),
    };

    /// <summary>HTTP 평문(localhost 가 아님)으로 SQL 과 이미지가 나가는 프로필이면 경고 한 줄을 보인다.</summary>
    public bool ShowPlainTextWarning => ShowProfileInfo && AiProfileUrl.IsPlainTextRemote(ActiveProfile?.BaseUrl);

    public string PlainTextWarningText => _t.T("이 주소는 암호화하지 않은 HTTP 입니다. SQL 과 이미지가 암호화 없이 전달됩니다.",
        "This address is plain HTTP. SQL and images are sent without encryption.");

    /// <summary>드롭다운 줄 목록: 시스템 CLI 묶음, 내 프로필 묶음, 새 프로필 줄. 선택이 바뀔 때마다 현재 줄 표시(<see cref="AiProfileChoice.IsCurrent"/>)를 새로 만든다.</summary>
    public IReadOnlyList<AiProfileChoice> Choices
    {
        get
        {
            var active = Active;
            var rows = new List<AiProfileChoice> { new(AiChoiceKind.Header, _t.T("시스템 CLI", "System CLI")) };
            foreach (var cli in AiProfilePresets.SystemClis)
            {
                rows.Add(cli.Available
                    ? new AiProfileChoice(AiChoiceKind.ClaudeCode, cli.DisplayName, IsCurrent: active.Kind == AiActiveKind.ClaudeCode)
                    : new AiProfileChoice(AiChoiceKind.DisabledCli, cli.DisplayName, _t.T("준비 중", "Coming soon")));
            }

            rows.Add(new AiProfileChoice(AiChoiceKind.Header, _t.T("내 프로필", "My profiles")));
            if (active.Kind == AiActiveKind.ProfileMissing)
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.MissingProfile, _t.T("삭제된 프로필", "Deleted profile"), ProfileId: active.MissingId, IsCurrent: true,
                    Reason: _t.T("찾을 수 없음", "Not found")));
            }

            foreach (var profile in _profiles)
            {
                var invalid = profile.Preset == AiProfilePreset.Unknown || profile.Format == AiApiFormat.Unknown;
                rows.Add(new AiProfileChoice(AiChoiceKind.Profile, profile.Name, profile.Model, profile.Id,
                    IsCurrent: active.Profile?.Id == profile.Id && profile.Id == ActiveProfileId,
                    Reason: invalid ? _t.T("이 버전에서 쓸 수 없음", "Not usable in this version") : null));
            }

            rows.Add(new AiProfileChoice(AiChoiceKind.NewProfile, _t.T("새 프로필 만들기…", "New profile…")));
            return rows;
        }
    }

    /// <summary>편집기 오른쪽 위에 보일 수 있는 한 줄(<c>AI: &lt;프로필 이름&gt; · &lt;모델&gt;</c>). 지금은 어떤 화면에도 바인딩하지 않는다(설계 §6.5).</summary>
    public string Summary => ActiveKind switch
    {
        AiActiveKind.None => _t.T("AI: 선택 안 함", "AI: None"),
        AiActiveKind.ClaudeCode => $"AI: Claude Code · {Model}",
        AiActiveKind.Profile => $"AI: {ActiveProfile!.Name} · {ActiveProfile.Model}",
        AiActiveKind.ProfileInvalid => $"AI: {ActiveProfile!.Name} · {_t.T("사용할 수 없음", "unusable")}",
        _ => _t.T("AI: 프로필 없음", "AI: profile missing"),
    };

    /// <summary>설정 파일에 쓸 <c>ai</c> 섹션(읽은 사본 기준). 「선택 안 함」 이면 모델을 비운다. effort 는 CLI 기본 줄이면 null.</summary>
    public AiSettings ToSettings() => ToSettings(_source);

    /// <summary>
    /// <paramref name="current"/>(저장소의 지금 값)에 이 쪽이 고른 값만 얹는다. 프로필 목록·실행 파일 경로는 current 의 값 그대로라서,
    /// 관리 창이 그사이 기록한 프로필을 설정 창 확인이 옛 사본으로 덮지 않는다.
    /// </summary>
    public AiSettings ToSettings(AiSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            Driver = Driver,
            ActiveProfileId = ActiveProfileId,
            Model = Driver == AiDriverKind.None ? null : Model,
            Effort = Effort == DefaultEffortText ? null : Effort,
            MaskLiterals = MaskLiterals,
            BenchmarkTimeoutSeconds = ParsedBenchmarkTimeout ?? current.BenchmarkTimeoutSeconds, // 올바르지 않은 글은 저장하지 않는다
        };
    }

    [RelayCommand]
    private void SelectNone() => IsNone = true;

    [RelayCommand]
    private void SelectDirect() => IsClaudeCode = true;

    [RelayCommand]
    private void ToggleDropdown() => IsDropdownOpen = !IsDropdownOpen;

    /// <summary>드롭다운에서 한 줄을 고른다. 새 프로필 줄은 관리 창만 열고 선택을 바꾸지 않으며, 고를 수 없는 줄은 무시한다.</summary>
    [RelayCommand]
    public void Choose(AiProfileChoice? choice)
    {
        IsDropdownOpen = false;
        if (choice is null || !choice.Selectable)
        {
            return;
        }

        switch (choice.Kind)
        {
            case AiChoiceKind.ClaudeCode:
                Notice = string.Empty;
                Driver = AiDriverKind.ClaudeCode;
                ActiveProfileId = null;
                break;
            case AiChoiceKind.Profile when choice.ProfileId is { } id:
                Notice = string.Empty;
                Driver = AiDriverKind.ClaudeCode;
                ActiveProfileId = id;
                break;
            case AiChoiceKind.NewProfile:
                ProfileManagerRequested?.Invoke(this, new ProfileManagerRequestedEventArgs(null, startNew: true));
                break;
        }
    }

    /// <summary>「프로필 관리」 단추: 지금 고른 프로필을 첫 줄로 하여 관리 창을 연다.</summary>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    private void ManageProfiles() =>
        ProfileManagerRequested?.Invoke(this, new ProfileManagerRequestedEventArgs(ActiveProfile?.Id, startNew: false));

    private bool CanManageProfiles() => Services is not null;

    /// <summary>
    /// 관리 창 모델을 만든다. 기록이 끝날 때마다 이 쪽 목록을 새로 받는다(<see cref="ApplyStoreChange"/>).
    /// 서비스가 없으면 null.
    /// </summary>
    /// <param name="confirm">삭제·변경 버림 확인(true = 진행).</param>
    public AiProfileManagerViewModel? CreateManager(ProfileManagerRequestedEventArgs request, Func<string, System.Threading.Tasks.Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirm);
        return Services is null
            ? null
            : new AiProfileManagerViewModel(Services, confirm, ApplyStoreChange, request.ProfileId, request.StartNew);
    }

    /// <summary>
    /// 관리 창이 프로필 목록을 기록한 뒤 호출한다. 목록만 새로 받고(<c>Driver</c>·모델·effort 등 미확정 선택은 그대로),
    /// <paramref name="selectNew"/> 가 있으면 방금 만든 프로필을 고른 상태로 둔다. 지운 프로필이 지금 고른 것이면 Claude Code 로 되돌리고 안내한다.
    /// </summary>
    internal void ApplyStoreChange(AiSettings latest, Guid? deletedId = null, Guid? selectNew = null)
    {
        ArgumentNullException.ThrowIfNull(latest);
        // 저장된 설정이 쓰던 프로필을 지운 경우(미확정 선택과 달라도)에도 Claude Code 로 되돌아갔음을 알린다.
        var wasStoredActive = deletedId is { } removed && _source.ActiveProfileId == removed;
        _source = latest;
        _profiles = latest.Profiles;
        if (deletedId is { } gone && ActiveProfileId == gone)
        {
            ActiveProfileId = null;
            Notice = _t.T("사용 중이던 프로필을 지워 Claude Code 로 되돌렸습니다.", "The profile in use was deleted, so Claude Code is selected again.");
        }
        else if (wasStoredActive)
        {
            Notice = _t.T("저장된 설정이 쓰던 프로필을 지워 저장된 선택이 Claude Code 로 바뀌었습니다. 확인을 누르면 지금 고른 선택이 저장됩니다.",
                "The profile the saved settings used was deleted, so the saved choice is now Claude Code. Pressing OK saves your current choice.");
        }
        else if (selectNew is { } id && Driver == AiDriverKind.ClaudeCode && _profiles.Any(p => p.Id == id))
        {
            ActiveProfileId = id;
            Notice = string.Empty;
        }

        // 활성 프로필 칸이 같아도 목록 내용(이름·모델·주소)이 바뀌었을 수 있으므로 파생 속성을 모두 다시 알린다.
        OnPropertyChanged(string.Empty);
        ProfilesPersisted?.Invoke(this, EventArgs.Empty);
    }
}
