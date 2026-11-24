using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.Choose;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Test;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>탭의 모양.</summary>
public enum AiHubState
{
    /// <summary>조회만 Role·AI 기능 꺼짐: 맨 위 큰 칸에 까닭과 「기능 탭에서 켜기」·「주 업무 바꾸기」, 아래 세 묶음은 접힘.</summary>
    Blocked,

    /// <summary>아직 연결하지 않음(「선택 안 함」): 맨 위 큰 칸에 「연결 도우미 시작」, 아래 세 묶음은 접힘.</summary>
    NotConnected,

    /// <summary>쓰는 대상이 있다: 세 묶음을 펼친다.</summary>
    Connected,

    /// <summary>골라 둔 대상을 쓸 수 없다(지워진 프로필·쓸 수 없는 CLI): 세 묶음을 펼치고 ②에 까닭.</summary>
    Problem,
}

/// <summary>설정 「AI」 탭이 쓰는 앱 부품. 시험은 가짜를 넣는다. 없으면(null) 그 단추는 꺼진다.</summary>
/// <param name="OpenWizard">도우미 창을 이 설정 창 위에 연다(주인 창·연 곳·까닭). 닫힐 때까지 기다린다.</param>
/// <param name="Access">「AI 가 볼 수 있는 접속」 읽기·저장(도우미의 접속 단계와 같은 길).</param>
/// <param name="AgentLink">외부 에이전트 연결 상태.</param>
/// <param name="OpenAgentConnect">「연결…」: 외부 에이전트 연결 창.</param>
/// <param name="OpenSkillInstall">「스킬 설치…」 창.</param>
/// <param name="Test">Claude Code·회사 서버 「연결 시험」 에 쓸 도우미 맥락(갈래·프로필은 탭이 채운다).</param>
/// <param name="CliTest">시스템 CLI·기타 CLI 「연결 시험」(짧은 질문 하나, <see cref="AiCliConnectionTest"/>).</param>
/// <param name="SkillStates">설치된 oranda 스킬 상태(③ 한 줄). 없으면 줄을 숨긴다.</param>
/// <param name="Shared">다른 곳(외부 에이전트 연결 창·접속 편집)의 수준 저장을 들을 공유 서비스.</param>
public sealed record AiSettingsHubServices(
    Func<Window?, AiSetupEntry, AiSetupErrorKind?, Task>? OpenWizard = null,
    AccessStepServices? Access = null,
    IExternalAgentLink? AgentLink = null,
    Func<Window?, Task>? OpenAgentConnect = null,
    Func<Window?, Task>? OpenSkillInstall = null,
    Func<AiSetupContext>? Test = null,
    Func<AiSettings, CancellationToken, Task<AiCliTestOutcome>>? CliTest = null,
    Func<IReadOnlyList<AgentSkillState>>? SkillStates = null,
    SharedAiAccessService? Shared = null);

/// <summary>
/// 설정 「AI」 탭(ai-settings-one-place 2026-11-24, 사용자가 확인한 시안 「권장대로」). 「누가 무엇을 보나」 순서:
/// <list type="number">
/// <item>① 「AI 가 볼 수 있는 접속」(공유): 접속마다 허용 수준 하나(<see cref="Access"/>, 도우미 단계와 같은 컨트롤). 아래 두 연결이 모두 이 표를 따른다.</item>
/// <item>② 「Oranda 안의 AI」: 상태·마지막 연결 시험·「연결 시험」·「다시 연결 (도우미)」, 접속별 실효 수준 한 줄, 접힌 고급 설정(<see cref="Model"/>).</item>
/// <item>③ 「밖의 AI 에이전트」: 상태·스킬 상태·「스킬 설치…」·「연결…」, min(접속 수준, 외부 상한) 한 줄, 「자기 프로그램·자기 로그인」 안내.</item>
/// </list>
/// 연결 전·막힘이면 맨 위 큰 칸이 먼저 나오고 세 묶음은 접힌다. 도우미는 이 탭에서 연 자식 창(설정 창 위 가운데)이고, 닫히면 저장소의 값으로 다시 읽는다
/// (「고급」 에 저장 안 한 변경이 있으면 버리기 전에 묻는다). 카드는 저장된 설정을 본다.
/// </summary>
public sealed partial class AiSettingsHubPage : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly AiSettingsText _t;
    private readonly Func<AiSettings, AiModelSettingsPage> _createModel;
    private readonly Func<bool> _aiFeatureOn;
    private readonly AiSettingsHubServices _services;
    private CancellationTokenSource? _testRun;
    private (AiSetupEntry Entry, AiSetupErrorKind? Reason, TaskCompletionSource Done)? _queued;
    private AiSettings _modelBaseline;
    private IReadOnlyList<AgentSkillState>? _skills;
    private bool _disposed;

    /// <param name="createModel">고급 설정 쪽 만들기(처음·도우미가 끝난 뒤 다시).</param>
    /// <param name="aiFeatureOn">AI 기능(aiTuning)이 지금 켜져 있나. 기본 = 저장된 기능 설정.</param>
    public AiSettingsHubPage(SettingsService settings, IShellText? text, Func<AiSettings, AiModelSettingsPage> createModel,
        AiSettingsHubServices? services = null, Func<bool>? aiFeatureOn = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _t = new AiSettingsText(text);
        _createModel = createModel ?? throw new ArgumentNullException(nameof(createModel));
        _services = services ?? new AiSettingsHubServices();
        _aiFeatureOn = aiFeatureOn ?? (() => FeatureCatalog.Resolve(_settings.Current.Features).Contains(FeatureIds.AiTuning));
        _model = createModel(settings.Current.Ai);
        _modelBaseline = _model.ToSettings(new AiSettings());
        if (_services.Access is { } access)
        {
            Access = new AccessStepViewModel(access, text) { AutoSave = true };
            Access.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AccessStepViewModel.Rows) or nameof(AccessStepViewModel.EnabledProfiles))
                {
                    OnPropertyChanged(nameof(InsideSummary));
                    OnPropertyChanged(nameof(OutsideSummary));
                }
            };
        }

        if (_services.AgentLink is { } link)
        {
            link.Changed += OnAgentChanged;
        }

        if (_services.Shared is { } shared)
        {
            shared.Changed += OnSharedChanged;
        }

        var open = State is AiHubState.Connected or AiHubState.Problem;
        _accessExpanded = open;
        _insideExpanded = open;
        _outsideExpanded = open;
    }

    /// <summary>고급 설정 쪽(예전 「AI 모델」 탭). 도우미가 끝나면 새 객체로 바뀐다(<see cref="ModelReplaced"/>).</summary>
    [ObservableProperty]
    private AiModelSettingsPage _model;

    /// <summary>고급 설정을 펼쳤다. 처음엔 접혀 있다.</summary>
    [ObservableProperty]
    private bool _advancedExpanded;

    /// <summary>① 펼침(연결됨이면 처음부터 펼침, 연결 전·막힘이면 한 줄로 접힘).</summary>
    [ObservableProperty]
    private bool _accessExpanded;

    /// <summary>② 펼침.</summary>
    [ObservableProperty]
    private bool _insideExpanded;

    /// <summary>③ 펼침.</summary>
    [ObservableProperty]
    private bool _outsideExpanded;

    /// <summary>도우미가 끝나 <see cref="Model"/> 을 새로 만들었다(설정 창이 비교 기준을 맞춘다).</summary>
    public event EventHandler? ModelReplaced;

    /// <summary>「기능 탭에서 켜기」·「주 업무 바꾸기」: 설정 창이 「기능」 탭으로 옮긴다. <c>true</c> = AI 튜닝을 켜 두고 옮긴다.</summary>
    public event EventHandler<bool>? FeaturesRequested;

    /// <summary>① 표(접속 서비스가 없으면 null — 그 묶음을 숨긴다).</summary>
    public AccessStepViewModel? Access { get; }

    public bool HasAccess => Access is not null;

    // ---- 상태 ---------------------------------------------------------------------------

    /// <summary>조회만 쓰는 Role(저장된 기능 설정).</summary>
    public bool IsReadOnlyRole => FeatureCatalog.IsReadOnly(_settings.Current.Features);

    public AiHubState State
    {
        get
        {
            if (IsReadOnlyRole || !_aiFeatureOn())
            {
                return AiHubState.Blocked;
            }

            return Saved.Kind switch
            {
                AiActiveKind.None => AiHubState.NotConnected,
                AiActiveKind.ClaudeCode or AiActiveKind.Profile or AiActiveKind.SystemCli or AiActiveKind.CustomCli => AiHubState.Connected,
                _ => AiHubState.Problem,
            };
        }
    }

    public bool IsBlocked => State == AiHubState.Blocked;

    public bool IsNotConnected => State == AiHubState.NotConnected;

    public bool IsConnected => State == AiHubState.Connected;

    public bool IsProblem => State == AiHubState.Problem;

    /// <summary>맨 위 큰 칸을 보인다(연결 전·막힘).</summary>
    public bool ShowsStartBox => IsBlocked || IsNotConnected;

    /// <summary>②의 상태·시험·도우미 줄을 보인다(연결됨·문제).</summary>
    public bool ShowsInsideStatus => IsConnected || IsProblem;

    private AiActiveProfile Saved => AiActiveProfile.Find(_settings.Current.Ai);

    private bool English => _t.IsEnglish;

    // ---- 맨 위 큰 칸(연결 전·막힘) ----------------------------------------------------------

    /// <summary>큰 칸 머리(연결 전·막힘). 연결됨이면 ②의 상태 줄과 같다.</summary>
    public string Headline => State switch
    {
        AiHubState.Blocked => IsReadOnlyRole ? ChooseText.ReadOnlyHeadline(English) : ChooseText.AiOffHeadline(English),
        AiHubState.NotConnected => _t.T("AI 가 아직 연결되지 않았습니다", "AI is not connected yet"),
        AiHubState.Problem => _t.T("골라 둔 AI 를 지금 쓸 수 없어요", "The selected AI cannot be used right now"),
        _ => InsideStatusText,
    };

    /// <summary>큰 칸 두 줄 안내·②의 설명 줄.</summary>
    public string Detail => State switch
    {
        AiHubState.Blocked => IsReadOnlyRole ? ChooseText.ReadOnlyDetail(English) : ChooseText.AiOffDetail(English),
        AiHubState.NotConnected => _t.T("「연결 도우미 시작」을 누르고 단추만 따라 누르면 몇 분 안에 끝납니다.\n회사 Claude 계정이나 회사 AI 서버 중에서 고르게 됩니다.",
            "Press \"Start setup assistant\" and follow the buttons; it takes a few minutes.\nYou choose the company Claude account or the company AI server."),
        AiHubState.Problem => ProblemText,
        _ => LastTestText,
    };

    private string ProblemText => Saved.Kind switch
    {
        AiActiveKind.ProfileMissing => _t.T("고른 프로필이 지워졌습니다. 도우미로 다시 연결하거나 아래 고급 설정에서 다른 모델을 고르세요.",
            "The selected profile was deleted. Connect again with the assistant, or pick another model in the advanced settings below."),
        AiActiveKind.ProfileInvalid => _t.T("고른 프로필을 이 버전이 읽지 못합니다. 도우미로 다시 연결하거나 아래 고급 설정에서 다른 모델을 고르세요.",
            "This version cannot read the selected profile. Connect again with the assistant, or pick another model in the advanced settings below."),
        _ => _t.T("고른 CLI 를 쓸 수 없습니다. 도우미로 다시 연결하거나 아래 고급 설정에서 다른 모델을 고르세요.",
            "The selected CLI is not available. Connect again with the assistant, or pick another model in the advanced settings below."),
    };

    public string StartWizardText => _t.T("연결 도우미 시작", "Start setup assistant");

    public string EnableFeatureText => _t.T("기능 탭에서 켜기", "Turn on in the Features tab");

    public string ChangeRoleText => _t.T("주 업무 바꾸기", "Change main work");

    /// <summary>막혔을 때 단추: 조회만이면 「주 업무 바꾸기」, 기능이 꺼졌으면 「기능 탭에서 켜기」.</summary>
    public string BlockedActionText => IsReadOnlyRole ? ChangeRoleText : EnableFeatureText;

    /// <summary>탭 맨 위 안내: 이 탭에서 정하는 세 가지와 처음 쓰는 사람이 할 일.</summary>
    public string IntroText => _t.T(
        "이 탭에서는 AI 에 관한 세 가지를 정합니다.\n"
        + "1  AI 가 어느 DB 접속을 어디까지 볼지\n"
        + "2  Oranda 안에서 쓰는 AI(SQL 설명 · 튜닝 제안 · AI 대화)\n"
        + "3  터미널의 AI 가 Oranda 를 쓰게 할지(개발자용)\n"
        + "처음이면 2 의 연결만 하면 됩니다. 1 과 3 은 처음 값 그대로 둬도 됩니다.",
        "This tab decides three things about AI.\n"
        + "1  Which DB connections AI can see, and how much\n"
        + "2  The AI used inside Oranda (SQL explain · tuning ideas · AI chat)\n"
        + "3  Whether AI in a terminal may use Oranda (for developers)\n"
        + "If this is your first time, only connect 2. You can leave 1 and 3 as they are.");

    // ---- ① AI 가 볼 수 있는 접속 ---------------------------------------------------------------

    public string AccessTitle => _t.T("AI 가 볼 수 있는 접속", "Connections AI can see");

    public string AccessNote => _t.T("아래 두 연결(2 · 3)이 모두 이 표를 따릅니다.", "Both connections below (2 and 3) follow this table.");

    /// <summary>① 표 읽는 법: 오른쪽일수록 더 많이 보임, 모르면 그대로, 운영 값은 가지 않음.</summary>
    public string AccessHelp => _t.T(
        "단추가 오른쪽으로 갈수록 AI 에 더 많이 보여 줍니다. 잘 모르겠으면 처음 값 그대로 두세요(개발 · 시험은 「계획」, 운영은 「조회 실행」).\n"
        + "운영 DB 의 실제 데이터 값은 회사 밖 AI(Claude 등)로 보내지 않습니다. 운영에서 SELECT 를 돌려도 그 AI 에는 몇 행이 나왔는지만 갑니다. "
        + "회사 안 AI 서버를 「로컬 AI」로 표시한 프로필이면 값까지 봅니다.",
        "The further right the button, the more AI is shown. If unsure, keep the starting value (Development and Test: \"Plan\", Production: \"Run SELECT\").\n"
        + "Actual data values from a production DB are not sent to AI outside the company (Claude and others). When a SELECT runs on production, that AI only gets the row count. "
        + "A profile that marks a company AI server as \"Local AI\" sees the values too.");

    /// <summary>접혀 있을 때(연결 전·막힘) 머리 옆 한 줄.</summary>
    public string AccessCollapsedHint => _t.T("도우미 마지막 단계에서 정합니다. 지금 바로 정해도 됩니다.", "The assistant sets this in its last step. You can also set it now.");

    // ---- ② Oranda 안의 AI ------------------------------------------------------------------

    public string InsideTitle => _t.T("Oranda 안의 AI", "AI inside Oranda");

    public string InsideSubtitle => _t.T(
        "편집기의 SQL 설명 · 튜닝 제안 · AI 대화가 쓰는 AI 입니다. 회사 Claude 계정이나 회사 AI 서버로 연결합니다.",
        "The AI behind SQL explain, tuning ideas and AI chat in the editor. It connects through a company Claude account or a company AI server.");

    /// <summary>② 단추 둘이 하는 일.</summary>
    public string InsideHelp => _t.T(
        "연결 시험: AI 에 짧은 질문을 보내 답이 오는지 봅니다(몇 초).\n"
        + "다시 연결: AI 가 안 되거나 계정 · 서버가 바뀌었을 때 도우미를 다시 엽니다.",
        "Test connection: sends AI a short question and checks that an answer comes back (a few seconds).\n"
        + "Connect again: reopens the assistant when AI stops working or your account or server changed.");

    /// <summary>고급 설정을 펼쳤을 때 맨 위 한 줄.</summary>
    public string AdvancedHint => _t.T(
        "보통은 바꿀 필요가 없습니다. 담당자가 알려 줄 때만 바꾸고, 바꾼 뒤에는 설정 창의 「저장」을 누르세요.",
        "You normally don't need to change these. Change them only when your administrator tells you to, then press \"Save\" in the settings window.");

    public string InsideCollapsedHint => _t.T("AI 를 연결하면 여기서 연결 시험과 다시 연결을 합니다.", "Once AI is connected, test and reconnect it here.");

    /// <summary>상태 알약(「연결됨 · Claude Code · sonnet」).</summary>
    public string InsideStatusText => State switch
    {
        AiHubState.Connected => _t.T("연결됨 · ", "Connected · ") + InUseTitle,
        AiHubState.Problem => _t.T("쓸 수 없음", "Unavailable"),
        AiHubState.Blocked => _t.T("꺼짐", "Off"),
        _ => _t.T("연결 안 됨", "Not connected"),
    };

    /// <summary>쓰는 대상의 이름(Claude Code 는 모델 이름까지).</summary>
    public string InUseTitle
    {
        get
        {
            var saved = Saved;
            var ai = _settings.Current.Ai;
            return saved.Kind switch
            {
                AiActiveKind.ClaudeCode => "Claude Code" + (string.IsNullOrWhiteSpace(ai.Model) ? string.Empty : " · " + ai.Model),
                AiActiveKind.Profile => saved.Profile!.Name + (string.IsNullOrWhiteSpace(saved.Profile.Model) ? string.Empty : " · " + saved.Profile.Model),
                AiActiveKind.SystemCli or AiActiveKind.CustomCli => AiCliConnectionTest.TargetName(saved) ?? string.Empty,
                _ => string.Empty,
            };
        }
    }

    /// <summary>마지막 연결 시험 한 줄.</summary>
    public string LastTestText
    {
        get
        {
            if (_testState == AiSetupTestState.Running)
            {
                return _t.T("연결을 시험하는 중…", "Testing the connection…");
            }

            if (_settings.Current.Ai.Setup.LastTest is not { } test)
            {
                return _t.T("아직 연결 시험을 하지 않았어요. 「연결 시험」을 눌러 보세요.", "No connection test yet. Press \"Test connection\".");
            }

            var when = test.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var seconds = (test.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            var kind = test.Error ?? AiSetupErrorKind.Unknown;
            var reason = test.Target == "Claude Code" || Saved.Kind is AiActiveKind.Profile ? AiSetupErrorText.For(kind, English) : AiCliConnectionTest.KindText(kind, test.Target, English);
            var result = test.Succeeded ? _t.T($"성공 ({seconds}초)", $"succeeded ({seconds} s)") : _t.T("실패 — ", "failed — ") + reason;
            return _t.T($"마지막 연결 시험: {when} · {test.Target} · {result}", $"Last connection test: {when} · {test.Target} · {result}");
        }
    }

    /// <summary>마지막 시험이 실패였다(경고색).</summary>
    public bool LastTestFailed => IsConnected && _testState != AiSetupTestState.Running && _settings.Current.Ai.Setup.LastTest is { Succeeded: false };

    /// <summary>「연결 시험」 을 할 수 있다: Claude Code·회사 서버 프로필(도우미 시험), 시스템 CLI·기타 CLI(짧은 질문).</summary>
    public bool CanTest => IsConnected && _testState != AiSetupTestState.Running && Saved.Kind switch
    {
        AiActiveKind.ClaudeCode or AiActiveKind.Profile => _services.Test is not null,
        AiActiveKind.SystemCli or AiActiveKind.CustomCli => _services.CliTest is not null,
        _ => false,
    };

    private AiSetupTestState _testState;

    public string ReconnectText => _t.T("다시 연결 (도우미)", "Connect again (assistant)");

    public string TestText => _t.T("연결 시험", "Test connection");

    /// <summary>회색 띠: 「위 1 의 표를 그대로 따릅니다: dev-erp 계획 · …」.</summary>
    public string InsideSummary => _t.T("위 1 의 표를 그대로 따릅니다: ", "Follows table 1 as is: ") + Levels(a => (a.Level, false));

    public string AdvancedTitle => _t.T("고급 설정 (모델 · 프로필 · 기타 CLI · 값 가림 …)", "Advanced settings (model · profiles · other CLIs · value masking …)");

    // ---- ③ 밖의 AI 에이전트 ----------------------------------------------------------------

    public string OutsideTitle => _t.T("밖의 AI 에이전트", "Outside AI agents");

    public string OutsideSubtitle => _t.T("터미널에서 띄운 Claude Code · Codex · Antigravity 가 Oranda 를 다루는 연결",
        "The connection that lets Claude Code, Codex or Antigravity started in a terminal work with Oranda");

    public string OutsideCollapsedHint => _t.T("필요한 사람만. 터미널에서 AI 를 쓰는 개발자용", "Only if you need it: for developers who use AI in a terminal");

    /// <summary>③ 쓰는 모습 예.</summary>
    public string OutsideExample => _t.T(
        "예) 터미널의 Claude Code 에 「어제 포장 실적 조회해 줘」라고 하면, 이 Oranda 에 새 탭을 만들고 SQL 을 넣어 실행합니다. 결과는 Oranda 그리드에 그대로 보입니다.",
        "Example: tell Claude Code in a terminal \"show yesterday's packing results\" and it opens a new tab in this Oranda, writes the SQL and runs it. The results appear in the Oranda grid.");

    /// <summary>③ 단추 둘이 하는 일.</summary>
    public string OutsideHelp => _t.T(
        "스킬 설치: 터미널 AI 에게 Oranda 쓰는 법을 알려 주는 안내 파일을 넣습니다. 처음 한 번, 그리고 「다시 설치가 필요」가 보일 때 누릅니다.\n"
        + "연결: 이 Oranda 를 터미널 AI 에 열어 줍니다. 정한 시간(기본 8시간)이 지나거나 상태 표시줄에서 「끊기」를 누르면 닫힙니다.",
        "Install skill: adds a guide file that tells the terminal AI how to use Oranda. Press it once at first, and again when \"needs reinstalling\" shows.\n"
        + "Connect: opens this Oranda to the terminal AI. It closes after the chosen time (8 hours by default) or when you press \"Disconnect\" in the status bar.");

    /// <summary>
    /// 회색 띠(Mac 시안 문구 2026-11-24): 「위 1 의 표를 그대로 따릅니다: dev-erp 계획 · prd-erp 조회 실행」. 수준은 min(접속 수준, 외부 상한)이고
    /// 상한이 실제로 낮출 때만 「(외부 상한)」 을 붙인다. 운영 접속이 있으면 둘째 줄 「운영 접속은 연결할 때 접속 이름을 한 번 더 입력해야 고를 수 있습니다」.
    /// </summary>
    public string OutsideSummary => _t.T("위 1 의 표를 그대로 따릅니다: ", "Follows table 1 as is: ")
        + Levels(a => (a.ExternalLevel, a.ExternalCapped))
        + (Access?.Rows.Any(r => r.IsProduction) == true
            ? "\n" + _t.T("운영 접속은 연결할 때 접속 이름을 한 번 더 입력해야 고를 수 있습니다.", "Production connections can be chosen only after typing the connection name once more when connecting.")
            : string.Empty);

    public string OutsideNote => _t.T("외부 에이전트는 자기 프로그램과 자기 로그인을 씁니다. Oranda 는 어느 접속을 어디까지 볼지만 정합니다.",
        "External agents use their own program and their own sign-in. Oranda only decides which connections they can see and how far.");

    /// <summary>외부 에이전트 연결 상태 한 줄.</summary>
    public string AgentStatusText
    {
        get
        {
            if (!_aiFeatureOn())
            {
                return _t.T("AI 기능이 꺼져 있어 연결할 수 없습니다.", "Not available while AI features are off.");
            }

            if (_services.AgentLink?.Current is not { } state)
            {
                return _t.T("연결되어 있지 않습니다.", "Not connected.");
            }

            var clients = state.Clients.Count == 0 ? _t.T("아직 붙은 CLI 없음", "no CLI attached yet") : string.Join(", ", state.Clients);
            var until = state.ExpiresAt is { } at
                ? " · " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + _t.T(" 까지", " until")
                : string.Empty;
            return _t.T("연결됨 · ", "Connected · ") + clients + until;
        }
    }

    /// <summary>스킬 상태 한 줄(설치됨·다시 설치 필요·아직 안 함). 알 수 없으면 빈 글.</summary>
    public string SkillStatusText => _skills switch
    {
        null => string.Empty,
        var s when s.Contains(AgentSkillState.Outdated) => _t.T("스킬: 다시 설치가 필요합니다(Oranda 위치나 판이 바뀜)", "Skill: needs reinstalling (Oranda moved or was updated)"),
        var s when s.Contains(AgentSkillState.Current) => _t.T($"스킬: 설치됨 ({s.Count(x => x == AgentSkillState.Current)}곳)", $"Skill: installed ({s.Count(x => x == AgentSkillState.Current)})"),
        _ => _t.T("스킬: 아직 설치하지 않았습니다", "Skill: not installed yet"),
    };

    public bool HasSkillStatus => _skills is not null;

    public bool CanOpenAgentConnect => _aiFeatureOn() && _services.OpenAgentConnect is not null;

    public bool CanOpenSkillInstall => _services.OpenSkillInstall is not null;

    public string AgentConnectText => _t.T("연결…", "Connect…");

    public string SkillInstallText => _t.T("스킬 설치…", "Install skill…");

    /// <summary>접속별 수준을 「이름 수준 · …」 로(①의 저장된 값). 접속이 없으면 「접속 없음」.</summary>
    private string Levels(Func<AiConnectionAccess, (AiAccessLevel Level, bool Capped)> pick)
    {
        if (Access is not { } access || access.Rows.Count == 0)
        {
            return _t.T("접속 없음", "no connections");
        }

        return string.Join(" · ", access.Rows.Select(r =>
        {
            var (level, capped) = pick(AiConnectionAccess.Of(r.Profile));
            return r.Name + " " + access.Text.LevelName(level) + (capped ? _t.T(" (외부 상한)", " (external cap)") : string.Empty);
        }));
    }

    // ---- 동작 ---------------------------------------------------------------------------

    /// <summary>지금 이 탭이 있는 창(도우미·연결 창의 주인). 뷰가 넣는다.</summary>
    internal Func<Window?> Owner { get; set; } = () => null;

    /// <summary>버리기 전에 묻는다(뷰가 확인 창을 넣는다). 기본 = 묻지 않고 버림.</summary>
    internal Func<string, Task<bool>> ConfirmDiscard { get; set; } = _ => Task.FromResult(true);

    /// <summary>도우미가 끝났는데 고급 설정에 저장 안 한 변경이 있을 때 묻는 글.</summary>
    public string DiscardQuestion => _t.T("저장 안 한 고급 설정 변경이 있습니다. 버리고 도우미가 고른 값을 쓸까요?",
        "There are unsaved changes in the advanced settings. Discard them and use the values the assistant chose?");

    /// <summary>고급 설정에 저장 안 한 변경이 있다(만들 때와 비교).</summary>
    public bool HasUnsavedAdvancedChanges => Model.ToSettings(new AiSettings()) != _modelBaseline;

    /// <summary>보여 달라고 한 묶음(뷰가 그 묶음을 화면에 들인다). 없으면 null.</summary>
    [ObservableProperty]
    private AiSettingsSection? _revealedSection;

    /// <summary>묶음 하나를 펼치고 화면에 들이라고 한다(상태 표시줄 띠 → ③).</summary>
    public void Reveal(AiSettingsSection section)
    {
        switch (section)
        {
            case AiSettingsSection.Access:
                AccessExpanded = true;
                break;
            case AiSettingsSection.Inside:
                InsideExpanded = true;
                break;
            default:
                OutsideExpanded = true;
                break;
        }

        RevealedSection = null; // 같은 묶음을 다시 불러도 알리게
        RevealedSection = section;
    }

    /// <summary>「연결 도우미 시작」·「다시 연결 (도우미)」.</summary>
    [RelayCommand]
    private Task StartWizardAsync() => RunWizardAsync(AiSetupEntry.Menu, reason: null);

    /// <summary>
    /// 도우미를 이 설정 창 위에 열고, 닫히면 저장소의 값으로 고급 설정·카드·표를 새로 읽는다.
    /// 고급 설정에 저장 안 한 변경이 있으면 버리기 전에 묻고, 「아니요」면 고급 설정은 그대로 두고 카드·표만 새로 읽는다.
    /// </summary>
    public async Task RunWizardAsync(AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (_services.OpenWizard is not { } open)
        {
            return;
        }

        var dirty = HasUnsavedAdvancedChanges;
        try
        {
            await open(Owner(), entry, reason).ConfigureAwait(true);
        }
        finally
        {
            var replace = !dirty || await ConfirmDiscard(DiscardQuestion).ConfigureAwait(true);
            await ReloadAsync(replace).ConfigureAwait(true);
        }
    }

    /// <summary>설정 창이 열리면 곧바로 도우미를 띄우라고 맡겨 둔다(첫 실행 묻기·실패 띠·옛 명령). 도우미가 닫히면 <paramref name="done"/> 이 끝난다.</summary>
    internal void QueueWizard(AiSetupEntry entry, AiSetupErrorKind? reason, TaskCompletionSource done) => _queued = (entry, reason, done);

    /// <summary>맡겨 둔 도우미가 있으면 띄운다(설정 창이 열린 뒤 한 번). 없으면 바로 끝난다.</summary>
    internal async Task RunQueuedWizardAsync()
    {
        if (_queued is not { } queued)
        {
            return;
        }

        _queued = null;
        try
        {
            await RunWizardAsync(queued.Entry, queued.Reason).ConfigureAwait(true);
        }
        finally
        {
            queued.Done.TrySetResult();
        }
    }

    /// <summary>맡겨 둔 도우미를 띄우지 못하고 창이 닫혔다(기다리는 쪽을 풀어 준다).</summary>
    internal void CancelQueuedWizard()
    {
        _queued?.Done.TrySetResult();
        _queued = null;
    }

    /// <summary>
    /// 저장소의 지금 값으로 다시 읽는다. <paramref name="replaceModel"/> 이면 고급 설정 쪽을 새로 만든다(미확정 AI 변경은 버려진다).
    /// 연결 전이었다가 연결됐으면 세 묶음을 펼친다.
    /// </summary>
    public async Task ReloadAsync(bool replaceModel = true)
    {
        if (_disposed)
        {
            return;
        }

        if (replaceModel)
        {
            Model = _createModel(_settings.Current.Ai);
            _modelBaseline = Model.ToSettings(new AiSettings());
            ModelReplaced?.Invoke(this, EventArgs.Empty);
        }

        if (State is AiHubState.Connected or AiHubState.Problem)
        {
            AccessExpanded = InsideExpanded = OutsideExpanded = true;
        }

        RefreshStatus();
        if (Access is { } access)
        {
            await access.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>① 표와 스킬 상태를 처음 읽는다(뷰가 붙을 때).</summary>
    internal async Task LoadAccessAsync()
    {
        if (_services.SkillStates is { } skills)
        {
            try
            {
                _skills = await Task.Run(skills).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _skills = null; // 스킬 폴더를 읽지 못하면 줄을 숨긴다
            }

            OnPropertyChanged(nameof(SkillStatusText));
            OnPropertyChanged(nameof(HasSkillStatus));
        }

        if (Access is { } access)
        {
            await access.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>카드 글을 모두 다시 알린다(설정 창 「저장」 뒤·시험 뒤).</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(string.Empty);
        TestCommand.NotifyCanExecuteChanged();
        OpenAgentConnectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 「연결 시험」: 저장된 대상으로 시험한다. Claude Code·회사 서버 프로필 = 도우미의 시험 단계와 같은 시험, 시스템 CLI·기타 CLI = 짧은 질문 하나.
    /// 결과는 설정(<c>ai.setup.lastTest</c>)에 남는다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var saved = Saved;
        _testRun?.Cancel();
        using var run = new CancellationTokenSource();
        _testRun = run;
        _testState = AiSetupTestState.Running;
        RefreshStatus();
        try
        {
            if (saved.Kind is AiActiveKind.SystemCli or AiActiveKind.CustomCli && _services.CliTest is { } cliTest)
            {
                var outcome = await cliTest(_settings.Current.Ai, run.Token).ConfigureAwait(true);
                _testState = outcome.Success ? AiSetupTestState.Succeeded : AiSetupTestState.Failed;
                await AiSetupTestStepViewModel.RecordAsync(_settings, new AiConnectionTestRecord(
                    DateTimeOffset.Now, outcome.Success, AiCliConnectionTest.TargetName(saved) ?? "CLI", (long)outcome.Elapsed.TotalMilliseconds,
                    outcome.Success ? null : outcome.Kind ?? AiSetupErrorKind.Unknown)).ConfigureAwait(true);
            }
            else if (_services.Test is { } makeContext)
            {
                var context = makeContext();
                context.ChosenPath = saved.Kind == AiActiveKind.Profile ? AiSetupPath.CompanyServer : AiSetupPath.ClaudeAccount;
                context.CreatedProfileId = saved.Profile?.Id;
                var vm = new AiSetupTestStepViewModel(context);
                await vm.RunAsync(run.Token).ConfigureAwait(true);
                _testState = vm.State;
            }
        }
        catch (OperationCanceledException)
        {
            _testState = AiSetupTestState.Idle;
        }
        finally
        {
            if (_testState == AiSetupTestState.Running)
            {
                _testState = AiSetupTestState.Idle;
            }

            if (ReferenceEquals(_testRun, run))
            {
                _testRun = null;
            }

            RefreshStatus();
        }
    }

    /// <summary>시험이 끝난 상태(시험용).</summary>
    internal AiSetupTestState TestState => _testState;

    /// <summary>막혔을 때 단추: 기능이 꺼졌으면 AI 튜닝을 켜 두고, 조회만이면 그냥 「기능」 탭으로.</summary>
    [RelayCommand]
    private void ResolveBlocked() => FeaturesRequested?.Invoke(this, !IsReadOnlyRole);

    [RelayCommand(CanExecute = nameof(CanOpenAgentConnect))]
    private async Task OpenAgentConnectAsync()
    {
        if (_services.OpenAgentConnect is { } open)
        {
            await open(Owner()).ConfigureAwait(true);
            RefreshStatus();
            if (Access is { } access)
            {
                await access.SyncAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSkillInstall))]
    private async Task OpenSkillInstallAsync()
    {
        if (_services.OpenSkillInstall is { } open)
        {
            await open(Owner()).ConfigureAwait(true);
            await LoadSkillsAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadSkillsAsync()
    {
        if (_services.SkillStates is not { } skills)
        {
            return;
        }

        try
        {
            _skills = await Task.Run(skills).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _skills = null;
        }

        OnPropertyChanged(nameof(SkillStatusText));
        OnPropertyChanged(nameof(HasSkillStatus));
    }

    private void OnAgentChanged(object? sender, EventArgs e) => UiThread.PostIfAppRunning(() => OnPropertyChanged(nameof(AgentStatusText)));

    /// <summary>다른 곳(외부 에이전트 연결 창·접속 편집·도우미)이 수준을 저장했다: ① 표와 ②·③ 요약을 맞춘다.</summary>
    private void OnSharedChanged(object? sender, AiConnectionAccess e) => UiThread.PostIfAppRunning(() =>
    {
        if (!_disposed && Access is { } access)
        {
            _ = access.SyncAsync(CancellationToken.None);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _testRun?.Cancel();
        CancelQueuedWizard();
        if (_services.AgentLink is { } link)
        {
            link.Changed -= OnAgentChanged;
        }

        if (_services.Shared is { } shared)
        {
            shared.Changed -= OnSharedChanged;
            shared.Dispose();
        }

        Access?.Dispose();
    }
}
