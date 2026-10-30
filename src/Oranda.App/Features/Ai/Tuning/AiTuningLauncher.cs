using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Binds;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 「AI 튜닝」 명령이 부르는 조립 한 곳: 편집기의 현재 문장과 보이는 세션의 접속으로 시작 확인 창을 띄우고, 확정되면 대화 창을 열어
/// <see cref="AiTuningFlow"/> 를 시작한다. 흐름은 대화 창 모델마다 하나만 만들어 쓴다(멈춤·이어 말하기 연결이 창당 한 번이라서).
/// </summary>
public sealed class AiTuningLauncher(IServiceProvider services)
{
    private AiChatViewModel? _flowChat;
    private AiTuningFlow? _flow;
    private ConnectionProfile? _profile;
    private readonly TuningStartBinds _binds = new();

    /// <summary>튜닝을 시작한다. 시작할 수 없거나(접속·문장 없음) 사용자가 취소하면 아무것도 하지 않는다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(IEditorContext)) is not IEditorContext editor
            || services.GetService(typeof(IAiDriver)) is not IAiDriver driver
            || services.GetService(typeof(IEventBus)) is not IEventBus bus
            || ExecutionState.For(bus).ShownProfile is not { } shown)
        {
            return;
        }

        if (_flow is { IsRunning: true })
        {
            await ShowChatAsync().ConfigureAwait(true); // 진행 중인 튜닝이 있다: 새로 시작하지 않고 그 창만 보인다
            return;
        }

        // 보이는 세션의 프로필은 접속할 때의 사본이라, 접속 편집에서 바꾼 AI 허용 수준·환경 라벨이 빠져 있다. 저장된 최신 값을 쓴다.
        var profile = await LatestProfileAsync(shown, ct).ConfigureAwait(true) ?? shown;

        var statement = editor.GetCurrentStatement();
        var settings = (services.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai ?? new AiSettings();
        var document = editor.GetActiveDocument();
        var tabId = document?.TabId.ToString("N", CultureInfo.InvariantCulture);
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
            }
        }
    }

    /// <summary>
    /// 대화 도구 창을 보이고 그 모델을 돌려준다. 창 모델은 처음 그릴 때 만들어지므로 한 번도 연 적 없으면 몇 차례 기다린다.
    /// </summary>
    private async Task<AiChatViewModel?> ShowChatAsync()
    {
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
        var configured = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
        var effort = settings.Effort?.Trim().ToLowerInvariant();
        var efforts = TuningStartInfo.EffortOptions(effort);
        return new TuningStartInfo(
            "AI 튜닝 — 커서 위치 문장",
            Summarize(statement),
            SharedPoolLine: null,
            SendFirst: "문장 글 · 접속 별칭 · 환경 라벨 · AI 허용 수준",
            SendOnRequest: "실제 계획 · 인덱스(열 순서·clustering factor) · 표·열 통계 · 히스토그램 개수",
            NotSent: "행 값 · 히스토그램 끝값 · 바인드 값(로컬 실행에만 씀) · 비밀번호·호스트",
            context,
            BuildModelOptions(configured),
            configured ?? TuningStartInfo.DefaultModelId,
            maskLiterals,
            efforts,
            efforts[0].Id == TuningStartInfo.DefaultModelId ? TuningStartInfo.DefaultModelId : effort!);
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
        _flow = new AiTuningFlow(
            editor,
            driver,
            chat,
            (title, text) => SqlEditorHost.Current.OpenTextTab(text, title),
            (context, vault) => _profile is { } profile && source is not null
                ? source.Track(new AgentSession(new AgentRunScope(Guid.NewGuid().ToString("N"), profile, context)
                {
                    Literals = vault,
                    MaskLiteralsRequired = AiAccessPolicy.MaskLiterals(profile, MaskLiteralsSetting()), // 사전이 없어도 게이트가 SQL 도구를 닫게 한다
                    Binds = _binds.Current, // 시작 때 담은 값. 로컬 실행에만 쓴다(AI 로 보내지 않는다)
                }))
                : null,
            RefreshContextAsync,
            services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker,
            context => context.Environment == ConnectionEnvironment.Production || _profile is not { } profile || AiAccessPolicy.MaskLiterals(profile, MaskLiteralsSetting()),
            new HostEditorTabs());
        _flow.Attach();
        return _flow;
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
