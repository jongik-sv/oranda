using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.Binds;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 「AI 튜닝」 명령이 부르는 조립 한 곳: 편집기의 현재 문장과 보이는 세션의 접속으로 시작 확인 창을 띄우고, 확정되면 대화 창을 열어
/// <see cref="AiTuningFlow"/> 를 시작한다. 흐름은 대화 창 모델마다 하나만 만들어 쓴다(멈춤·이어 말하기 연결이 창당 한 번이라서).
/// </summary>
public sealed class AiTuningLauncher(IServiceProvider services)
{
    private static readonly ConditionalWeakTable<IServiceProvider, AiTuningLauncher> Launchers = [];

    private AiChatViewModel? _flowChat;
    private AiChatViewModel? _wiredChat;
    private IDisposable? _settingsSubscription;
    private AiTuningFlow? _flow;
    private ConnectionProfile? _profile;
    private readonly TuningStartBinds _binds = new();

    // 이 대화의 운영 벤치마크 제한 시간(초). 시작 창에서 확정한 값이며 이어 말하기도 같은 값을 쓴다(설정에는 저장하지 않는다).
    // 아직 시작한 적이 없으면 기본 30초로 닫는다(제한 없음은 사용자가 0 으로 명시한 경우에만).
    private int _benchmarkTimeoutSeconds = AiSettings.DefaultBenchmarkTimeoutSeconds;

    /// <summary>시험용: 도구 창을 띄우지 않고 쓸 대화 창 모델을 직접 준다(전역 <see cref="AiChatViewModel.Current"/> 를 거치지 않게).</summary>
    internal Func<Task<AiChatViewModel?>>? ChatOverride { get; set; }

    /// <summary>튜닝을 시작하다 예외가 났을 때 화면에 보이는 고정 글(상세는 가린 뒤 추적 기록에만 남긴다).</summary>
    public const string StartFailedText = "AI 튜닝을 시작하지 못했습니다. 자세한 원인은 로그에 남겼습니다.";

    /// <summary>활성 문서가 지금 접속의 SQL 탭이 아니라 튜닝을 시작하지 않았을 때 대화 창에 남기는 안내.</summary>
    public const string OtherDocumentText = "활성 문서가 지금 접속의 SQL 탭이 아니라서 튜닝을 시작하지 않았습니다(PL/SQL 편집기·객체 상세이거나 다른 접속의 탭). 튜닝할 SQL 탭을 열고 문장 위에 커서를 둔 뒤 다시 시도하세요.";

    /// <summary>서비스 모음마다 하나인 조립(명령 처리기와 대화 창이 같은 것을 쓴다: 흐름은 대화 창당 하나라서).</summary>
    public static AiTuningLauncher For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Launchers.GetValue(services, s => new AiTuningLauncher(s));
    }

    /// <summary>
    /// 튜닝을 시작한다. 시작할 수 없거나(접속·문장 없음) 사용자가 취소하면 아무것도 하지 않는다. 시작하다 예외가 나면 대화 창에 원인을 남긴다
    /// (명령 실행 경로 <c>RegistryCommand</c> 는 예외를 추적 기록에만 남기므로, 여기서 잡지 않으면 사용자에게 아무것도 보이지 않는다).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(ct).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 어떤 실패든 명령 경로에서 삼켜지기 전에 대화 창에 보인다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceError("AI 튜닝 시작 중 예외: {0}", AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
            await ReportFailureAsync(StartFailedText).ConfigureAwait(true);
        }
    }

    /// <summary>명령(SQL 설명·실행 계획 해설·오류 고치기)을 시작하다 예외가 났을 때 화면에 보이는 고정 글.</summary>
    public const string CommandStartFailedText = "AI 명령을 시작하지 못했습니다. 자세한 원인은 로그에 남겼습니다.";

    /// <summary>활성 문서가 지금 접속의 SQL 탭이 아니라 명령을 보내지 않았을 때 대화 창에 남기는 안내.</summary>
    public const string CommandOtherDocumentText = "활성 문서가 지금 접속의 SQL 탭이 아니라서 보내지 않았습니다(PL/SQL 편집기·객체 상세이거나 다른 접속의 탭). SQL 탭을 열고 다시 시도하세요.";

    /// <summary>
    /// AI 명령 하나를 시작한다(시작 확인 창 없이 바로). 보낼 수 없거나 명령이 거부하면 대화 창에 이유만 남기고 앞 대화는 건드리지 않는다.
    /// 시작하다 예외가 나면 <see cref="RunAsync"/> 와 같이 대화 창에 고정 글을 남긴다.
    /// </summary>
    public async Task RunCommandAsync(AiCommandSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        try
        {
            await RunCommandCoreAsync(spec, ct).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 어떤 실패든 명령 경로에서 삼켜지기 전에 대화 창에 보인다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceError("AI 명령 시작 중 예외({0}): {1}", spec.Id, AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
            await ReportFailureAsync(CommandStartFailedText).ConfigureAwait(true);
        }
    }

    private async Task ReportFailureAsync(string text)
    {
        try
        {
            if (await ShowChatAsync().ConfigureAwait(true) is { } chat)
            {
                chat.AddAssistantText(text);
            }
        }
#pragma warning disable CA1031 // 알리는 일마저 실패하면 더 할 수 있는 것이 없다(추적 기록은 위에서 남겼다).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceError("AI 튜닝 오류를 대화 창에 남기지 못했습니다: {0}", ex);
        }
    }

    /// <summary>튜닝과 명령이 같이 쓰는 시작 앞부분의 결과.</summary>
    private sealed record StartScope(
        IEditorContext Editor,
        IAiDriver Driver,
        ConnectionProfile Profile,
        AiSettings Settings,
        EditorSnapshot? Document,
        string? TabId,
        AiStatementSource Source,
        SqlStatement? Statement);

    /// <summary>
    /// 시작 앞부분 공통: 서비스·보이는 세션 확인, 진행 중이면 그 창만 보임, 활성 문서가 지금 접속의 SQL 탭인지 확인(아니면 <paramref name="otherDocumentText"/> 를 알림),
    /// 저장된 최신 프로필 읽기. 시작하지 않을 때 null.
    /// </summary>
    private async Task<StartScope?> ResolveStartAsync(string otherDocumentText, CancellationToken ct)
    {
        if (services.GetService(typeof(IEditorContext)) is not IEditorContext editor
            || services.GetService(typeof(IAiDriver)) is not IAiDriver driver
            || services.GetService(typeof(IEventBus)) is not IEventBus bus
            || ExecutionState.For(bus).ShownProfile is not { } shown)
        {
            return null;
        }

        if (_flow is { IsRunning: true })
        {
            await ShowChatAsync().ConfigureAwait(true); // 진행 중인 작업이 있다: 새로 시작하지 않고 그 창만 보인다
            return null;
        }

        // 활성 문서가 지금 접속의 SQL 탭이 아니면(PL/SQL 편집기·객체 상세·다른 접속의 탭, 또는 판단 불가) 시작 창을 띄우지 않고 알린다:
        // 편집기 맥락은 마지막 SQL 탭의 문장을 돌려주므로 그대로 두면 다른 접속 탭의 문장이 지금 접속의 가림·허용 수준 아래로 나간다. 일반 대화와 같은 판단이다.
        var (source, statement) = AiStatementScope.Resolve(editor, SqlDocumentActive(), shown.Id);
        if (source == AiStatementSource.OtherDocument)
        {
            await ReportFailureAsync(otherDocumentText).ConfigureAwait(true);
            return null;
        }

        // 보이는 세션의 프로필은 접속할 때의 사본이라, 접속 편집에서 바꾼 AI 허용 수준·환경 라벨이 빠져 있다. 저장된 최신 값을 쓴다.
        var profile = await LatestProfileAsync(shown, ct).ConfigureAwait(true) ?? shown;
        await ReloadAiSettingsAsync(ct).ConfigureAwait(true);
        var settings = (services.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai ?? new AiSettings();
        var document = editor.GetActiveDocument();
        var tabId = document?.TabId.ToString("N", CultureInfo.InvariantCulture);
        return new StartScope(editor, driver, profile, settings, document, tabId, source, statement);
    }

    private async Task RunCommandCoreAsync(AiCommandSpec spec, CancellationToken ct)
    {
        if (await ResolveStartAsync(CommandOtherDocumentText, ct).ConfigureAwait(true) is not { } scope)
        {
            return;
        }

        var (editor, driver, profile, settings, document, tabId, source, statement) = scope;
        var context = new AiContext(profile.Name, profile.Environment, AiAccessPolicy.Effective(profile), tabId, statement?.Text);
        if (context.AccessLevel == AiAccessLevel.Off)
        {
            await ReportFailureAsync(AiTuningFlow.CommandAccessOffText).ConfigureAwait(true);
            return;
        }

        var env = new AiCommandStartEnv(
            services, editor, profile, context, source, statement, document?.TabId, AiAccessPolicy.MaskLiterals(profile, settings.MaskLiterals));
        var preparation = await spec.PrepareAsync(env, ct).ConfigureAwait(true);
        if (preparation.Target is not { } target)
        {
            await ReportFailureAsync(preparation.RefusalText ?? CommandStartFailedText).ConfigureAwait(true);
            return;
        }

        AgentBridgeStartup.Start(services); // 시작 작업이 이미 열었으면 아무 일도 없다
        if (await ShowChatAsync().ConfigureAwait(true) is not { } chat)
        {
            return;
        }

        // 명령은 바인드 값을 쓰지 않는다(문장을 실행하지 않고, 도구 범위는 빈 바인드 사전). 대화의 접속은 시작이 확정될 때만 바뀐다: 거부되면 앞 값으로 되돌린다.
        var flow = FlowFor(chat, editor, driver);
        var previous = _profile;
        var generation = flow.Generation;
        _profile = profile;
        try
        {
            await flow.StartCommandAsync(spec, target, context, ct).ConfigureAwait(true);
        }
        finally
        {
            if (flow.Generation == generation)
            {
                _profile = previous;
            }
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        if (await ResolveStartAsync(OtherDocumentText, ct).ConfigureAwait(true) is not { } scope)
        {
            return;
        }

        var (editor, driver, profile, settings, document, tabId, _, statement) = scope;

        // 기타 CLI 는 DB 도구가 없어 튜닝에 쓰지 않는다: 시작 창을 띄우기 전에 안내하고 닫는다(흐름의 시작도 같은 판단으로 한 번 더 막는다).
        if (AiTuningCustomCli.IsSelected(settings))
        {
            await ReportFailureAsync(AiTuningCustomCli.TuningText).ConfigureAwait(true);
            return;
        }

        var context = new AiContext(profile.Name, profile.Environment, AiAccessPolicy.Effective(profile), tabId, statement?.Text);
        var info = BuildStartInfo(statement, context, settings, AiAccessPolicy.MaskLiterals(profile, settings.MaskLiterals));

        var owner = (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow;
        var choice = owner is null ? null : await TuningStartDialog.ShowAsync(owner, new TuningStartViewModel(info)).ConfigureAwait(true);
        if (choice is null)
        {
            return;
        }

        AgentBridgeStartup.Start(services); // 시작 작업이 이미 열었으면 아무 일도 없다
        if (await ShowChatAsync().ConfigureAwait(true) is not { } chat)
        {
            return;
        }

        // 대화의 접속은 시작이 확정될 때만 바뀐다. 문장 없음·가림 불가로 거부된 시작이 프로필을 바꿔 두면 앞 대화의 이어 말하기가 새 접속으로 간다.
        // 바인드 값도 시작 탭에서 이때 한 번 담는다(이어 말하기 때 다른 탭의 값이 섞이지 않게). 시작이 거부되면 앞 대화의 값으로 되돌린다.
        var flow = FlowFor(chat, editor, driver);
        var previous = _profile;
        var generation = flow.Generation;
        _profile = profile;
        var active = SqlEditorHost.Current.Active;
        var previousTimeout = CaptureBenchmarkTimeout(choice.BenchmarkTimeoutSeconds);
        var previousBinds = _binds.Capture(statement, document is not null && active?.TabId == document.TabId ? active.Binds : null);
        try
        {
            await flow.StartAsync(choice, context, ct).ConfigureAwait(true);
        }
        finally
        {
            if (flow.Generation == generation)
            {
                _profile = previous;
                _binds.Restore(previousBinds);
                _benchmarkTimeoutSeconds = previousTimeout; // 거부된 시작이 앞 대화의 제한 시간을 바꿔 두지 않게
            }
        }
    }

    /// <summary>
    /// 대화 창 모델을 AI 에 잇는다(창당 한 번, 되풀이해도 된다): 설정을 따르는 이미지 입구·모델 이름·사용 불가 원인 표시, 그리고 튜닝 없이 쓰는 일반 대화의 보내기.
    /// 대화 창이 처음 그려질 때 부른다 — 튜닝을 시작하지 않아도 입력칸이 처음부터 열려 있게.
    /// </summary>
    public void EnsureChat(AiChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (ReferenceEquals(_wiredChat, chat))
        {
            return;
        }

        _wiredChat = chat;
        chat.SettingsProvider = CurrentAiSettings; // 이미지 입구·모델 이름이 지금 설정의 활성 프로필을 따른다
        chat.UnavailableProvider = UnavailableReason;
        chat.RefreshActiveModel();
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _settingsSubscription?.Dispose();
            _settingsSubscription = bus.Subscribe<SettingsChanged>(this, e =>
            {
                if (e.Section == "ai")
                {
                    Dispatcher.UIThread.Post(chat.RefreshActiveModel);
                }
            });
        }

        chat.FollowUpRequested += async (_, followUp) => await FollowUpAsync(chat, followUp).ConfigureAwait(true);
    }

    /// <summary>
    /// AI 를 쓸 수 없는 원인(없으면 null). 입력칸은 이 원인이 있어도 열려 있지만, 보내면 이 원인을 대화 창에 남기고 글을 입력칸에 돌려 둔다.
    /// </summary>
    private string? UnavailableReason()
    {
        if (services.GetService(typeof(IFeatureGate)) is IFeatureGate gate && !gate.IsEnabled(FeatureIds.AiTuning))
        {
            return "AI 기능이 꺼져 있습니다. 기능 설정에서 AI 튜닝을 켜면 보낼 수 있습니다.";
        }

        if (services.GetService(typeof(IAiDriver)) is null)
        {
            return "AI 드라이버를 쓸 수 없습니다(이 빌드에 없습니다).";
        }

        return CurrentAiSettings() is { } settings
            ? AiActiveProfile.Find(settings).Kind switch
            {
                AiActiveKind.None => "AI 모델이 「선택 안 함」 입니다. 설정에서 AI 모델을 고르면 보낼 수 있습니다.",
                AiActiveKind.ProfileMissing => "선택한 AI 프로필을 찾을 수 없습니다. 설정에서 다른 프로필을 고르십시오.",
                AiActiveKind.ProfileInvalid => "선택한 AI 프로필의 형식을 이 버전이 알지 못합니다. 설정에서 다른 프로필을 고르십시오.",
                AiActiveKind.CustomCliUnavailable => AiTuningCustomCli.UnavailableText,
                _ => null,
            }
            : null;
    }

    /// <summary>
    /// 보내기 전에 파일의 AI 설정을 다시 읽는다: 다른 앱 창이 저장했어도 확인 창·기대값과 라우터가 같은 설정을 본다.
    /// 읽지 못하면 앞의 값을 두고 넘어간다(라우터가 같은 파일을 읽어 <c>settings_unreadable</c> 로 막는다).
    /// </summary>
    private async Task ReloadAiSettingsAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return;
        }

        try
        {
            await settings.ReloadAiAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("AI 설정을 다시 읽지 못했습니다: {0}", AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
            return;
        }

        _wiredChat?.RefreshActiveModel();
    }

    /// <summary>대화 창에서 보낸 글(일반 대화 또는 튜닝 이어 말하기)을 흐름으로 넘긴다. 못 보내면 원인을 남기고 글을 입력칸에 돌려 둔다.</summary>
    private async Task FollowUpAsync(AiChatViewModel chat, ChatFollowUp followUp)
    {
        try
        {
            await ReloadAiSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            if (chat.UnavailableReason is { } reason)
            {
                chat.AddAssistantText("오류: " + reason);
                chat.RestoreFollowUp(followUp);
                return;
            }

            if (services.GetService(typeof(IEditorContext)) is not IEditorContext editor || services.GetService(typeof(IAiDriver)) is not IAiDriver driver)
            {
                chat.AddAssistantText("오류: AI 를 쓸 수 없습니다(편집기나 AI 드라이버가 없습니다).");
                chat.RestoreFollowUp(followUp);
                return;
            }

            AgentBridgeStartup.Start(services); // 시작 작업이 이미 열었으면 아무 일도 없다
            await FlowFor(chat, editor, driver).FollowUpAsync(followUp).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 이벤트 처리기라 예외를 밖으로 내면 앱 전역 미처리 예외가 된다: 대화 창에 알린다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceError("이어 말하기 중 예외: {0}", AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
            chat.AddAssistantText(AiTuningFlow.FollowUpFailedText);
        }
    }

    /// <summary>시험용: 지금 일반 대화·튜닝이 도구 범위에 쓰는 접속.</summary>
    internal ConnectionProfile? ActiveProfile
    {
        get => _profile;
        set => _profile = value;
    }

    /// <summary>
    /// 일반 대화(시작 창이 없다)가 쓰는 제한 시간. 설정값이되, 0(제한 없음)은 경고를 보고 정한 값이 아니므로 30초로 닫는다. 설정을 못 읽어도 30초다.
    /// </summary>
    internal static int GeneralChatBenchmarkTimeout(int? configured) =>
        configured is { } seconds and > 0 ? AiSettings.NormalizeBenchmarkTimeout(seconds) : AiSettings.DefaultBenchmarkTimeoutSeconds;

    /// <summary>시작이 확정될 때 시작 창의 제한 시간(초)을 담고, 시작이 거부되면 되돌릴 앞 값을 돌려준다. 범위 밖 값은 30초가 된다.</summary>
    internal int CaptureBenchmarkTimeout(int seconds)
    {
        var previous = _benchmarkTimeoutSeconds;
        _benchmarkTimeoutSeconds = AiSettings.NormalizeBenchmarkTimeout(seconds);
        return previous;
    }

    /// <summary>시험용: 튜닝 시작 때 확정한 운영 벤치마크 제한 시간(초).</summary>
    internal int StartedBenchmarkTimeoutSeconds => _benchmarkTimeoutSeconds;

    /// <summary>시험용: 튜닝 시작 때 담은 바인드 값.</summary>
    internal TuningStartBinds StartedBinds => _binds;

    /// <summary>시험용: 세션 등록(앱의 에이전트 세션 출처 대신).</summary>
    internal Func<AgentSession, AgentSession>? SessionTracker { get; set; }

    /// <summary>시험용: 활성 문서가 SQL 작업 공간 문서인지 알려 주는 쪽(기본은 세션 라우터).</summary>
    internal Func<bool?>? SqlDocumentActiveProvider { get; set; }

    private bool? SqlDocumentActive() =>
        SqlDocumentActiveProvider is { } provided ? provided() : SessionRouter.Find(services)?.SqlDocumentActive;

    /// <summary>
    /// 도구 실행 범위. 일반 대화는 튜닝의 바인드 값(앞 튜닝의 시작 탭 값)을 싣지 않고 빈 사전을 쓴다. 등록할 곳이 없으면 null(도구 없이 보낸다).
    /// </summary>
    internal AgentSession? NewSessionFor(bool general, AiContext context, LiteralVault? vault)
    {
        var source = services.GetService(typeof(IAgentSessionSource)) as AppAgentSessionSource;
        // 일반 대화는 시작 창을 거치지 않으므로 설정의 기본값을 쓴다(앞 튜닝에서 이번 실행에만 바꾼 값이 섞이지 않게). 설정이 0 이어도 30초로 닫는다.
        return NewSession(source, context, vault, general ? new Dictionary<string, BindValue>() : _binds.Current,
            general ? GeneralChatBenchmarkTimeout(CurrentAiSettings()?.BenchmarkTimeoutSeconds) : _benchmarkTimeoutSeconds);
    }

    /// <summary>시험용: 이 대화 창 모델이 AI 에 이어져 있다.</summary>
    internal bool IsWired(AiChatViewModel chat) => ReferenceEquals(_wiredChat, chat);

    /// <summary>
    /// 일반 대화의 접속 정보: 보이는 세션의 접속을 저장소의 최신 값으로 읽어 이름·환경·허용 수준을 만든다. 접속이 없으면 문맥은 null(DB 도구 없이 답한다)이고
    /// 가리는 대화인지는 전역 가림 설정을 따른다. 접속이 있는데 저장소를 읽을 수 없거나 저장소에서 사라졌으면 접속을 믿지 않고(문맥 null) 가리는 대화로 본다.
    /// 접속이 있으면 도구 실행 범위가 쓸 <c>_profile</c> 도 이 접속으로 맞춘다.
    /// </summary>
    internal async Task<AiGeneralScope> GeneralContextAsync(CancellationToken ct)
    {
        _profile = null;
        var globalMask = MaskLiteralsSetting();
        var shownProfile = services.GetService(typeof(IEventBus)) is IEventBus bus ? ExecutionState.For(bus).ShownProfile : null;
        if (shownProfile is not { } shown)
        {
            return new AiGeneralScope(null, globalMask, UnsafeBinding: await UnsafeBindingAsync(ct).ConfigureAwait(true));
        }

        var sqlDocumentActive = SqlDocumentActive();
        if (await LatestProfileAsync(shown, ct, failClosed: true).ConfigureAwait(true) is not { } latest)
        {
            return new AiGeneralScope(null, Masking: true, shown.Id, sqlDocumentActive); // 알 수 없으면 가리는 대화로(닫힌 쪽)
        }

        _profile = latest;
        return new AiGeneralScope(
            new AiContext(latest.Name, latest.Environment, AiAccessPolicy.Effective(latest)),
            AiAccessPolicy.MaskLiterals(latest, globalMask),
            latest.Id,
            sqlDocumentActive,
            UnsafeBinding: await UnsafeBindingAsync(ct).ConfigureAwait(true)); // 보이는 접속이 있어도 접속을 기다리는 PL/SQL 문서는 닫는다
    }

    /// <summary>
    /// 보이는 접속이 없는데 활성 SQL 탭이 접속이 닫힌 접속에 묶여 있고(<see cref="AiGeneralScope.UnsafeBinding"/>) 그 접속이 운영이거나 저장소에서 찾을 수 없으면(읽지 못함 포함) 참.
    /// 저장소 최신 환경 라벨을 보며, 알 수 없으면 닫는다. PL/SQL 문서가 활성이면 보이는 접속이 그 문서의 세션을 따라가므로 따로 보지 않는다(문서가 아직 세션을 갖지 못했으면 접속을 알 수 없어 닫는다).
    /// </summary>
    private async Task<bool> UnsafeBindingAsync(CancellationToken ct)
    {
        var router = SessionRouter.Find(services);

        // 접속을 기다리는 PL/SQL 문서(세션이 아직 없다)가 활성이면 그 문서의 접속을 알 수 없다: 닫는다.
        if (router?.ConnectionDocument is { Session: null })
        {
            return true;
        }

        var missing = router?.ShownMissing;
        if (missing is not { } id)
        {
            return false;
        }

        if (services.GetService(typeof(IConnectionStore)) is not IConnectionStore store)
        {
            return true;
        }

        try
        {
            var profile = (await store.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles.FirstOrDefault(p => p.Id == id);
            return IsUnsafe(profile);
        }
#pragma warning disable CA1031 // 저장소를 못 읽으면 환경을 알 수 없다: 닫는다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return true;
        }

        static bool IsUnsafe(ConnectionProfile? profile) => profile is null || profile.Environment == ConnectionEnvironment.Production;
    }

    /// <summary>
    /// 대화 도구 창을 보이고 그 모델을 돌려준다. 창 모델은 처음 그릴 때 만들어지므로 한 번도 연 적 없으면 몇 차례 기다린다.
    /// </summary>
    private async Task<AiChatViewModel?> ShowChatAsync()
    {
        if (ChatOverride is { } provided)
        {
            return await provided().ConfigureAwait(true);
        }

        (services.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.aiChat");
        for (var i = 0; i < 5 && AiChatViewModel.Current is null; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        return AiChatViewModel.Current;
    }

    /// <summary>
    /// 접속 저장소에 있는 이 접속의 지금 프로필. 저장소에서 사라졌으면 null. 저장소를 읽을 수 없으면 <paramref name="known"/> 을 그대로 돌려주되,
    /// <paramref name="failClosed"/> 이면(이어 말하기: 낡은 환경 라벨로 가림 검사를 지나치면 안 된다) null.
    /// </summary>
    private async Task<ConnectionProfile?> LatestProfileAsync(ConnectionProfile known, CancellationToken ct, bool failClosed = false)
    {
        if (services.GetService(typeof(IConnectionStore)) is not IConnectionStore store)
        {
            return known;
        }

        try
        {
            var document = await store.LoadAsync(ct).ConfigureAwait(true);
            return document.Value.Profiles.FirstOrDefault(p => p.Id == known.Id);
        }
#pragma warning disable CA1031 // 저장소를 못 읽어도 튜닝을 막지 않고 알고 있는 사본으로 계속한다(허용 수준은 시작 확인 창이 다시 보인다).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return failClosed ? null : known;
        }
    }

    /// <summary>이어 말하기 때: 접속의 지금 설정으로 문맥을 다시 만든다. 접속이 사라졌으면 null.</summary>
    private async Task<AiContext?> RefreshContextAsync(AiContext old, CancellationToken ct)
    {
        if (_profile is not { } known || await LatestProfileAsync(known, ct, failClosed: true).ConfigureAwait(true) is not { } latest)
        {
            return null;
        }

        _profile = latest;
        return old with { ConnectionName = latest.Name, Environment = latest.Environment, AccessLevel = AiAccessPolicy.Effective(latest) };
    }

    /// <summary>
    /// 시작 탭의 바인드 값 중 대상 문장이 쓰는 이름의 값(값을 못 만든 이름·비밀로 보이는 이름은 뺀다). AI 로 보내지 않고 벤치마크 실행에만 쓴다.
    /// 문장이나 목록이 없으면 빈 사전이다(바인드가 있는 변형은 빠진 이름을 밝혀 실패한다).
    /// </summary>
    public static IReadOnlyDictionary<string, BindValue> StartBinds(SqlStatement? statement, BindVariableStore? list)
    {
        if (statement is null || list is null)
        {
            return new Dictionary<string, BindValue>();
        }

        return ExportBinds.Resolve(statement, null, list).Binds
            .Where(pair => !SensitiveBindNames.IsSensitive(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>시작 확인 창에 보일 사실을 만든다(시안 9). 문장을 못 찾으면 요약만 비운다.</summary>
    public static TuningStartInfo BuildStartInfo(SqlStatement? statement, AiContext context, AiSettings settings, bool maskLiterals = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var active = AiActiveProfile.Find(settings);
        var profile = active.Kind == AiActiveKind.Profile ? active.Profile : null;
        var configured = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
        var systemCli = active.Kind is AiActiveKind.SystemCli or AiActiveKind.SystemCliUnavailable or AiActiveKind.CustomCli or AiActiveKind.CustomCliUnavailable; // 시스템 CLI·기타 CLI 에는 Claude 의 모델·노력 줄을 만들지 않는다
        var apiProfile = systemCli || profile is { Format: AiApiFormat.OpenAi }; // API 경로·시스템 CLI 는 effort 를 쓰지 않는다
        var effort = apiProfile ? null : settings.Effort?.Trim().ToLowerInvariant();
        var efforts = TuningStartInfo.EffortOptions(effort);
        var models = systemCli ? [new TuningModelOption(TuningStartInfo.DefaultModelId, active.SystemCli?.DisplayName ?? CustomCliLabel(active) ?? "기본 모델")]
            : profile is null ? BuildModelOptions(configured) : BuildProfileModelOptions(profile);
        return new TuningStartInfo(
            "AI 튜닝 — 커서 위치 문장",
            Summarize(statement),
            SharedPoolLine: null,
            SendFirst: "문장 글 · 접속 별칭 · 환경 라벨 · AI 허용 수준",
            SendOnRequest: "실제 계획 · 인덱스(열 순서·clustering factor) · 표·열 통계 · 히스토그램 개수",
            NotSent: "행 값 · 히스토그램 끝값 · 바인드 값(로컬 실행에만 씀) · 비밀번호·호스트",
            context,
            models,
            systemCli ? TuningStartInfo.DefaultModelId : profile is null ? configured ?? TuningStartInfo.DefaultModelId : models[0].Id,
            maskLiterals,
            efforts,
            efforts[0].Id == TuningStartInfo.DefaultModelId ? TuningStartInfo.DefaultModelId : effort!,
            ShowEffort: !apiProfile,
            BenchmarkTimeoutSeconds: settings.BenchmarkTimeoutSeconds);
    }

    /// <summary>기타 CLI 가 활성이면 그 이름(이름이 비면 id). 아니면 null.</summary>
    private static string? CustomCliLabel(AiActiveProfile active) =>
        active.CustomCli is { } custom ? (custom.Name.Trim().Length > 0 ? custom.Name.Trim() : custom.Id) : null;

    /// <summary>프로필이 활성이면 모델 줄은 그 프로필의 모델 하나뿐이다(Claude Code 의 opus·sonnet·haiku 는 프로필 서버에 맞지 않는다). 모델이 비었으면 지정하지 않는 줄.</summary>
    public static IReadOnlyList<TuningModelOption> BuildProfileModelOptions(AiLlmProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var model = profile.Model?.Trim() ?? string.Empty;
        return model.Length == 0
            ? [new TuningModelOption(TuningStartInfo.DefaultModelId, $"{profile.Name} · 모델 미지정")]
            : [new TuningModelOption(model, model)];
    }

    /// <summary>
    /// 고를 수 있는 모델 줄: opus·sonnet·haiku. 설정 값이 이 목록에 없으면 그 값을 줄로 더하고,
    /// 설정 값이 비어 있으면 Claude Code 의 기본 모델 줄을 맨 앞에 둔다(그 줄은 모델을 지정하지 않고 보낸다).
    /// </summary>
    public static IReadOnlyList<TuningModelOption> BuildModelOptions(string? configured)
    {
        List<TuningModelOption> options = [];
        if (configured is null)
        {
            options.Add(new TuningModelOption(TuningStartInfo.DefaultModelId, "기본 모델 — Claude Code"));
        }

        foreach (var known in AiModelSettingsPage.KnownModels)
        {
            options.Add(new TuningModelOption(known, known));
        }

        if (configured is not null && !AiModelSettingsPage.KnownModels.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new TuningModelOption(configured, configured));
        }

        return options;
    }

    private AiTuningFlow FlowFor(AiChatViewModel chat, IEditorContext editor, IAiDriver driver)
    {
        if (_flow is not null && ReferenceEquals(_flowChat, chat))
        {
            return _flow;
        }

        var source = services.GetService(typeof(IAgentSessionSource)) as AppAgentSessionSource;
        _flowChat = chat;
        EnsureChat(chat);

        _flow = new AiTuningFlow(
            editor,
            driver,
            chat,
            (title, text, profile) => SqlEditorHost.Current.OpenTextTab(text, title, profile),
            (context, vault) => NewSessionFor(general: false, context, vault), // 시작 때 담은 값. 로컬 실행에만 쓴다(AI 로 보내지 않는다)
            RefreshContextAsync,
            services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker,
            context => context.Environment == ConnectionEnvironment.Production || _profile is not { } profile || AiAccessPolicy.MaskLiterals(profile, MaskLiteralsSetting()),
            new HostEditorTabs(),
            (title, before, after) => SqlEditorHost.Current.Dialogs.ShowComparisonAsync(title, before, after, CancellationToken.None),
            ReadIndexesAsync,
            (edit, before, token) => SqlEditorHost.Current.Dialogs.PreviewEditTextAsync(edit.Description ?? "제안된 편집", before, edit.NewText, token),
            CurrentAiSettings,
            ConfirmImagesAsync,
            AuditImagesAsync,
            GeneralContextAsync,
            (context, vault) => NewSessionFor(general: true, context, vault), // 일반 대화는 바인드 값이 없다(앞 튜닝의 값이 섞이지 않게)
            ConfirmTextAsync,
            services.GetService(typeof(IAgentGate)) as IAgentGate); // 기타 CLI 명령의 문맥 미리 읽기(도구와 같은 관문)
        _flow.Attach(followUps: false); // 이어 말하기는 EnsureChat 이 먼저 받아 넘긴다(사용할 수 없는 원인을 확인하려고)
        return _flow;
    }

    private AgentSession? NewSession(AppAgentSessionSource? source, AiContext context, LiteralVault? vault, IReadOnlyDictionary<string, BindValue> binds, int benchmarkTimeoutSeconds) =>
        _profile is { } profile && (SessionTracker is not null || source is not null)
            ? (SessionTracker ?? source!.Track)(new AgentSession(new AgentRunScope(Guid.NewGuid().ToString("N"), profile, context)
            {
                Literals = vault,
                MaskLiteralsRequired = AiAccessPolicy.MaskLiterals(profile, MaskLiteralsSetting()), // 사전이 없어도 게이트가 SQL 도구를 닫게 한다
                Binds = binds,
                BenchmarkTimeoutSeconds = benchmarkTimeoutSeconds,
            }))
            : null;

    /// <summary>가리는 일반 대화에서 입력한 글이 가려지지 않고 나간다는 확인 창. 주 창이 없으면 보내지 않는다.</summary>
    private async Task<bool> ConfirmTextAsync(AiImageSendNotice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow is { } owner
            && await AiTextWarningDialog.ShowAsync(owner, notice).ConfigureAwait(true);
    }

    /// <summary>지금 설정의 <c>ai</c> 섹션(설정 서비스가 없으면 null).</summary>
    private AiSettings? CurrentAiSettings() => (services.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai;

    /// <summary>가리는 대화에 새 이미지를 보내기 전 확인 창(시안 ChatWarning). 주 창이 없으면 보내지 않는다.</summary>
    private async Task<bool> ConfirmImagesAsync(AiImageSendNotice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow is { } owner
            && await AiImageWarningDialog.ShowAsync(owner, notice).ConfigureAwait(true);
    }

    /// <summary>
    /// 새 이미지를 보낸다는 사실을 감사 기록에 남긴다. 합성 도구 이름 <see cref="ImageAuditTool"/> 로 장수·크기·받는 곳만 쓰고 이미지 내용은 쓰지 않는다
    /// (게이트 밖의 경로라 게이트가 쓰는 같은 저장소의 같은 행 형식을 따른다).
    /// </summary>
    private async Task AuditImagesAsync(AiImageSendNotice notice, CancellationToken ct)
    {
        if (services.GetService(typeof(IAgentAuditStore)) is not IAgentAuditStore store)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await store.AppendAsync(BuildImageAuditEntry(notice, _profile, DateTimeOffset.UtcNow), timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
    }

    /// <summary>감사 기록에 합성 도구로 남기는 이름.</summary>
    public const string ImageAuditTool = "chat_image_send";

    /// <summary>
    /// 새 이미지 전송의 감사 행. <c>Arguments</c> 는 장수·바이트·받는 곳, <c>Reason</c> 은 가리는 대화에서 사용자가 확인 창을 거쳤음(<c>unmasked_images_confirmed</c>)을 뜻한다.
    /// 이미지 내용·API 키는 담지 않는다.
    /// </summary>
    public static AgentAuditEntry BuildImageAuditEntry(AiImageSendNotice notice, ConnectionProfile? connection, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return new AgentAuditEntry
        {
            OccurredAt = now,
            ProfileId = connection?.Id,
            ProfileName = notice.ConnectionName,
            Client = "oranda-chat",
            RunId = Guid.NewGuid().ToString("N"),
            Tool = ImageAuditTool,
            Arguments = string.Create(CultureInfo.InvariantCulture, $"images={notice.Count}; bytes={notice.TotalBytes}; to={notice.Recipient}"),
            AccessLevel = notice.AccessLevel,
            Verdict = AiToolJudgement.Allowed,
            Reason = notice.Masking ? "unmasked_images_confirmed" : null,
        };
    }

    /// <summary>
    /// AI 인덱스 후보를 대조할 기존 인덱스 열 구성(TN-09). 대화의 접속으로 AI 조회용 세션을 빌려 읽고,
    /// 접속·서비스가 없거나 못 읽으면 null(대조를 건너뛴다). 소유자를 모르면 접속 사용자.
    /// </summary>
    private async Task<IReadOnlyList<IndexColumnsRow>?> ReadIndexesAsync(string owner, string table, CancellationToken ct)
    {
        if (_profile is not { } profile
            || services.GetService(typeof(IAgentSessionSource)) is not AppAgentSessionSource source
            || services.GetService(typeof(ITuningService)) is not ITuningService tuning)
        {
            return null;
        }

        using var session = new AgentSession(new AgentRunScope(Guid.NewGuid().ToString("N"), profile, new AiContext(profile.Name, profile.Environment, AiAccessPolicy.Effective(profile))));
        var db = await source.AcquireDbSessionAsync(session, ct).ConfigureAwait(true);
        var effectiveOwner = owner.Length > 0 ? owner : profile.User?.ToUpperInvariant() ?? string.Empty;
        return await tuning.GetIndexColumnsAsync(db, effectiveOwner, table, ct).ConfigureAwait(true);
    }

    /// <summary>설정의 리터럴 가림 값(운영 접속은 이 값과 관계없이 항상 가린다: <see cref="AiAccessPolicy.MaskLiterals"/>).</summary>
    private bool MaskLiteralsSetting() =>
        (services.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai.MaskLiterals ?? false;

    private static string Summarize(SqlStatement? statement)
    {
        if (statement is null)
        {
            return string.Empty;
        }

        var lines = statement.Text.Split('\n').Length;
        var oneLine = statement.Text.ReplaceLineEndings(" ").Trim();
        return $"{lines}줄 · " + (oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…");
    }
}

/// <summary>
/// 대화 하나가 쓰는 바인드 값: 시작이 확정될 때 시작 탭에서 한 번 담고(사본), 이어 말하기는 이 값만 쓴다.
/// 그래서 시작 뒤에 다른 탭을 활성으로 바꾸거나 목록 값을 고쳐도 대화의 값은 바뀌지 않는다(바꾸려면 튜닝을 다시 시작한다).
/// </summary>
internal sealed class TuningStartBinds
{
    public IReadOnlyDictionary<string, BindValue> Current { get; private set; } = new Dictionary<string, BindValue>();

    /// <summary>시작 탭의 값을 담고, 시작이 거부되면 되돌릴 앞 값을 돌려준다.</summary>
    public IReadOnlyDictionary<string, BindValue> Capture(SqlStatement? statement, BindVariableStore? list)
    {
        var previous = Current;
        Current = AiTuningLauncher.StartBinds(statement, list);
        return previous;
    }

    /// <summary>시작이 거부됐을 때 앞 대화의 값으로 되돌린다.</summary>
    public void Restore(IReadOnlyDictionary<string, BindValue> previous) => Current = previous ?? throw new ArgumentNullException(nameof(previous));
}
