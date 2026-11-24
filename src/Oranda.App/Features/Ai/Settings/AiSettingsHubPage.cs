using System;
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
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>「연결 상태」 카드의 모양.</summary>
public enum AiHubState
{
    /// <summary>조회만 Role·AI 기능 꺼짐: 까닭과 「기능 켜기」·「주 업무 바꾸기」.</summary>
    Blocked,

    /// <summary>아직 연결하지 않음(「선택 안 함」): 쉬운 안내와 큰 「연결 도우미 시작」.</summary>
    NotConnected,

    /// <summary>쓰는 대상이 있다: 이름·마지막 시험·「연결 시험」·「다시 연결(도우미)」.</summary>
    Connected,

    /// <summary>골라 둔 대상을 쓸 수 없다(지워진 프로필·쓸 수 없는 CLI): 까닭과 도우미·고급.</summary>
    Problem,
}

/// <summary>설정 「AI」 탭이 쓰는 앱 부품. 시험은 가짜를 넣는다. 없으면(null) 그 단추는 꺼진다.</summary>
/// <param name="OpenWizard">도우미 창을 이 설정 창 위에 연다(주인 창·연 곳·까닭). 닫힐 때까지 기다린다.</param>
/// <param name="Access">「AI 를 쓸 접속」 읽기·저장(도우미의 접속 단계와 같은 길).</param>
/// <param name="AgentLink">외부 에이전트 연결 상태.</param>
/// <param name="OpenAgentConnect">「연결…」: 외부 에이전트 연결 창.</param>
/// <param name="OpenSkillInstall">「스킬 설치…」 창.</param>
/// <param name="Test">「연결 시험」 에 쓸 도우미 맥락을 만든다(갈래·프로필은 탭이 채운다). null 이면 시험을 못 한다.</param>
public sealed record AiSettingsHubServices(
    Func<Window?, AiSetupEntry, AiSetupErrorKind?, Task>? OpenWizard = null,
    AccessStepServices? Access = null,
    IExternalAgentLink? AgentLink = null,
    Func<Window?, Task>? OpenAgentConnect = null,
    Func<Window?, Task>? OpenSkillInstall = null,
    Func<AiSetupContext>? Test = null);

/// <summary>
/// 설정 「AI」 탭(ai-settings-one-place 2026-11-24): AI 에 관한 설정을 한곳에서 시작한다.
/// <list type="number">
/// <item>맨 위 「연결 상태」 카드(<see cref="State"/>): 저장된 설정이 쓰는 대상·마지막 연결 시험·도우미 단추. 막혔으면 까닭과 「기능 켜기」·「주 업무 바꾸기」.</item>
/// <item>「AI 를 쓸 접속」: 도우미의 접속 단계와 같은 화면·같은 값(스위치를 바꾸면 바로 저장).</item>
/// <item>「외부 에이전트」: 연결 상태, 「연결…」, 「스킬 설치…」.</item>
/// <item>「고급」(처음엔 접힘): 지금까지의 「AI 모델」 쪽(<see cref="Model"/>) 그대로 — 주·가벼운 일 모델, CLI 모델·effort, 프로필 관리, 기타 CLI, 값 가림, 조인 문법, 벤치 제한 시간.</item>
/// </list>
/// 도우미는 이 탭에서 연 자식 창으로 돈다(설정 창 위). 닫히면 저장소의 값으로 <see cref="Model"/> 을 새로 만들어 방금 고른 값이 보인다.
/// 카드는 저장된 설정을 본다(「고급」 에서 고친 값은 설정 창 「저장」 뒤에 카드에 들어간다).
/// </summary>
public sealed partial class AiSettingsHubPage : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IShellText? _text;
    private readonly AiSettingsText _t;
    private readonly Func<AiSettings, AiModelSettingsPage> _createModel;
    private readonly Func<bool> _aiFeatureOn;
    private readonly AiSettingsHubServices _services;
    private CancellationTokenSource? _testRun;
    private (AiSetupEntry Entry, AiSetupErrorKind? Reason, TaskCompletionSource Done)? _queued;
    private bool _disposed;

    /// <param name="createModel">「고급」 쪽 만들기(처음·도우미가 끝난 뒤 다시).</param>
    /// <param name="aiFeatureOn">AI 기능(aiTuning)이 지금 켜져 있나. 기본 = 저장된 기능 설정.</param>
    public AiSettingsHubPage(SettingsService settings, IShellText? text, Func<AiSettings, AiModelSettingsPage> createModel,
        AiSettingsHubServices? services = null, Func<bool>? aiFeatureOn = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _text = text;
        _t = new AiSettingsText(text);
        _createModel = createModel ?? throw new ArgumentNullException(nameof(createModel));
        _services = services ?? new AiSettingsHubServices();
        _aiFeatureOn = aiFeatureOn ?? (() => FeatureCatalog.Resolve(_settings.Current.Features).Contains(FeatureIds.AiTuning));
        _model = createModel(settings.Current.Ai);
        if (_services.Access is { } access)
        {
            Access = new AccessStepViewModel(access, text) { AutoSave = true };
        }

        if (_services.AgentLink is { } link)
        {
            link.Changed += OnAgentChanged;
        }
    }

    /// <summary>「고급」 쪽(예전 「AI 모델」 탭). 도우미가 끝나면 새 객체로 바뀐다(<see cref="ModelReplaced"/>).</summary>
    [ObservableProperty]
    private AiModelSettingsPage _model;

    /// <summary>「고급」 을 펼쳤다. 처음엔 접혀 있다.</summary>
    [ObservableProperty]
    private bool _advancedExpanded;

    /// <summary>도우미가 끝나 <see cref="Model"/> 을 새로 만들었다(설정 창이 비교 기준을 맞춘다).</summary>
    public event EventHandler? ModelReplaced;

    /// <summary>「기능 켜기」·「주 업무 바꾸기」: 설정 창이 「기능」 탭으로 옮긴다. <c>true</c> = AI 튜닝을 켜 두고 옮긴다.</summary>
    public event EventHandler<bool>? FeaturesRequested;

    /// <summary>「AI 를 쓸 접속」(접속 서비스가 없으면 null — 그 묶음을 숨긴다).</summary>
    public AccessStepViewModel? Access { get; }

    public bool HasAccess => Access is not null;

    // ---- 연결 상태 카드 ----------------------------------------------------------------

    /// <summary>조회만 쓰는 Role(저장된 기능 설정).</summary>
    public bool IsReadOnlyRole => FeatureCatalog.IsReadOnly(_settings.Current.Features);

    /// <summary>카드 모양.</summary>
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

    /// <summary>도우미·시험을 쓸 수 있다(막히지 않음).</summary>
    public bool CanUseAi => !IsBlocked;

    private AiActiveProfile Saved => AiActiveProfile.Find(_settings.Current.Ai);

    public string StatusTitle => _t.T("연결 상태", "Connection status");

    /// <summary>카드 머리 한 줄.</summary>
    public string Headline => State switch
    {
        AiHubState.Blocked => IsReadOnlyRole ? ChooseText.ReadOnlyHeadline(English) : ChooseText.AiOffHeadline(English),
        AiHubState.NotConnected => _t.T("아직 AI 를 연결하지 않았어요", "AI is not connected yet"),
        AiHubState.Problem => _t.T("골라 둔 AI 를 지금 쓸 수 없어요", "The selected AI cannot be used right now"),
        _ => _t.T("사용 중: ", "In use: ") + InUseTitle,
    };

    /// <summary>카드 설명.</summary>
    public string Detail => State switch
    {
        AiHubState.Blocked => IsReadOnlyRole ? ChooseText.ReadOnlyDetail(English) : ChooseText.AiOffDetail(English),
        AiHubState.NotConnected => _t.T("「연결 도우미 시작」을 누르고 단추만 따라 누르면 몇 분 안에 끝납니다. 회사 Claude 계정이나 회사 AI 서버 중에서 고르게 됩니다.",
            "Press \"Start setup assistant\" and follow the buttons; it takes a few minutes. You choose the company Claude account or the company AI server."),
        AiHubState.Problem => ProblemText,
        _ => LastTestText,
    };

    private string ProblemText => Saved.Kind switch
    {
        AiActiveKind.ProfileMissing => _t.T("고른 프로필이 지워졌습니다. 도우미로 다시 연결하거나 아래 「고급」에서 다른 모델을 고르세요.",
            "The selected profile was deleted. Connect again with the assistant, or pick another model under Advanced below."),
        AiActiveKind.ProfileInvalid => _t.T("고른 프로필을 이 버전이 읽지 못합니다. 도우미로 다시 연결하거나 아래 「고급」에서 다른 모델을 고르세요.",
            "This version cannot read the selected profile. Connect again with the assistant, or pick another model under Advanced below."),
        _ => _t.T("고른 CLI 를 쓸 수 없습니다. 도우미로 다시 연결하거나 아래 「고급」에서 다른 모델을 고르세요.",
            "The selected CLI is not available. Connect again with the assistant, or pick another model under Advanced below."),
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
                AiActiveKind.SystemCli => saved.SystemCli!.DisplayName,
                AiActiveKind.CustomCli => string.IsNullOrWhiteSpace(saved.CustomCli!.Name) ? saved.CustomCli.Id : saved.CustomCli.Name,
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
            var result = test.Succeeded
                ? _t.T($"성공 ({seconds}초)", $"succeeded ({seconds} s)")
                : _t.T("실패 — ", "failed — ") + AiSetupErrorText.For(test.Error ?? AiSetupErrorKind.Unknown, English);
            return _t.T($"마지막 연결 시험: {when} · {test.Target} · {result}", $"Last connection test: {when} · {test.Target} · {result}");
        }
    }

    /// <summary>마지막 시험이 실패였다(경고색).</summary>
    public bool LastTestFailed => IsConnected && _testState != AiSetupTestState.Running && _settings.Current.Ai.Setup.LastTest is { Succeeded: false };

    /// <summary>「연결 시험」 을 할 수 있는 대상(Claude Code·회사 서버 프로필). 다른 CLI 는 쓸 때 확인한다.</summary>
    public bool CanTest => IsConnected && Saved.Kind is AiActiveKind.ClaudeCode or AiActiveKind.Profile && _services.Test is not null && _testState != AiSetupTestState.Running;

    /// <summary>시험할 수 없는 대상일 때의 한 줄(시스템·기타 CLI).</summary>
    public string TestUnavailableText => _t.T("이 CLI 는 연결 시험 대신 AI 를 쓸 때 확인합니다.", "This CLI is checked when you use AI instead of by a connection test.");

    public bool ShowTestUnavailable => IsConnected && Saved.Kind is AiActiveKind.SystemCli or AiActiveKind.CustomCli;

    private AiSetupTestState _testState;

    public string StartWizardText => _t.T("연결 도우미 시작", "Start setup assistant");

    public string ReconnectText => _t.T("다시 연결(도우미)", "Connect again (assistant)");

    public string TestText => _t.T("연결 시험", "Test connection");

    public string EnableFeatureText => _t.T("기능 켜기", "Turn on the feature");

    public string ChangeRoleText => _t.T("주 업무 바꾸기", "Change main work");

    /// <summary>막혔을 때 단추: 조회만이면 「주 업무 바꾸기」, 기능이 꺼졌으면 「기능 켜기」.</summary>
    public string BlockedActionText => IsReadOnlyRole ? ChangeRoleText : EnableFeatureText;

    public string AccessTitle => _t.T("AI 를 쓸 접속", "Connections that use AI");

    public string AccessHint => _t.T("도우미의 「AI 를 쓸 접속」과 같은 값입니다. 스위치를 바꾸면 바로 저장됩니다.",
        "Same values as the assistant's \"Connections that use AI\" step. Switching saves at once.");

    public string AgentTitle => _t.T("외부 에이전트", "External agents");

    public string AgentHint => _t.T("밖의 LLM CLI(Claude Code·Codex 등)가 orandactl 로 이 창을 다루게 합니다.",
        "Lets an outside LLM CLI (Claude Code, Codex and others) work with this window through orandactl.");

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
                ? _t.T(" · ", " · ") + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + _t.T(" 까지", " until")
                : string.Empty;
            return _t.T("연결됨 · ", "Connected · ") + clients + until;
        }
    }

    public bool CanOpenAgentConnect => _aiFeatureOn() && _services.OpenAgentConnect is not null;

    public bool CanOpenSkillInstall => _services.OpenSkillInstall is not null;

    public string AgentConnectText => _t.T("연결…", "Connect…");

    public string SkillInstallText => _t.T("스킬 설치…", "Install skill…");

    public string AdvancedTitle => _t.T("고급", "Advanced");

    public string AdvancedHint => _t.T("주·가벼운 일 모델, CLI 모델·effort, 프로필 관리, 기타 CLI, 값 가림, 조인 문법, 벤치마크 제한 시간",
        "Main and light-task models, CLI model and effort, profiles, other CLIs, value masking, join syntax, benchmark time limit");

    private bool English => _t.IsEnglish;

    // ---- 동작 ---------------------------------------------------------------------------

    /// <summary>지금 이 탭이 있는 창(도우미·연결 창의 주인). 뷰가 넣는다.</summary>
    internal Func<Window?> Owner { get; set; } = () => null;

    /// <summary>「연결 도우미 시작」·「다시 연결(도우미)」.</summary>
    [RelayCommand]
    private Task StartWizardAsync() => RunWizardAsync(AiSetupEntry.Menu, reason: null);

    /// <summary>
    /// 도우미를 이 설정 창 위에 열고, 닫히면 저장소의 값으로 「고급」 과 카드·접속 목록을 새로 읽는다.
    /// </summary>
    public async Task RunWizardAsync(AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (_services.OpenWizard is not { } open)
        {
            return;
        }

        try
        {
            await open(Owner(), entry, reason).ConfigureAwait(true);
        }
        finally
        {
            await ReloadAsync().ConfigureAwait(true);
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
    /// 저장소의 지금 값으로 다시 읽는다: 「고급」 쪽을 새로 만들고(설정 창의 미확정 AI 변경은 버려진다 — 도우미가 방금 저장한 값이 이긴다), 카드·접속 목록을 고친다.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (_disposed)
        {
            return;
        }

        Model = _createModel(_settings.Current.Ai);
        ModelReplaced?.Invoke(this, EventArgs.Empty);
        RefreshStatus();
        if (Access is { } access)
        {
            await access.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>접속 목록을 처음 읽는다(뷰가 붙을 때).</summary>
    internal Task LoadAccessAsync() => Access?.LoadAsync(CancellationToken.None) ?? Task.CompletedTask;

    /// <summary>카드 글을 모두 다시 알린다(설정 창 「저장」 뒤·시험 뒤).</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(string.Empty);
        TestCommand.NotifyCanExecuteChanged();
        OpenAgentConnectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>「연결 시험」: 저장된 대상(Claude Code 또는 회사 서버 프로필)으로 도우미의 시험 단계와 같은 시험을 한다. 결과는 설정에 남는다.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (_services.Test is not { } makeContext)
        {
            return;
        }

        var saved = Saved;
        var context = makeContext();
        context.ChosenPath = saved.Kind == AiActiveKind.Profile ? AiSetupPath.CompanyServer : AiSetupPath.ClaudeAccount;
        context.CreatedProfileId = saved.Profile?.Id;
        var vm = new AiSetupTestStepViewModel(context);
        _testRun?.Cancel();
        using var run = new CancellationTokenSource();
        _testRun = run;
        _testState = AiSetupTestState.Running;
        RefreshStatus();
        try
        {
            await vm.RunAsync(run.Token).ConfigureAwait(true);
        }
        finally
        {
            _testState = vm.State;
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
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSkillInstall))]
    private async Task OpenSkillInstallAsync()
    {
        if (_services.OpenSkillInstall is { } open)
        {
            await open(Owner()).ConfigureAwait(true);
        }
    }

    private void OnAgentChanged(object? sender, EventArgs e) => UiThread.PostIfAppRunning(() => OnPropertyChanged(nameof(AgentStatusText)));

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

        Access?.Dispose();
    }
}
