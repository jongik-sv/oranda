using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Plans;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// <c>oranda.panel.plan</c> — 실행 계획 창. 창은 하나이고, 활성 SQL 탭이 바뀌면 그 탭의 계획(<see cref="PlanViewModel"/>)으로 바꿔 보인다
/// (<see cref="Oranda.App.Features.Execution.ResultsPanelViewModel"/> 과 같은 방식). 탭마다 계획 트리·텍스트·고른 줄·접힘·sql_id 칸·상태 글이 따로 남는다.
/// 계획을 만드는 작업은 시작한 탭의 모델에 결과를 쓰므로, 실행 중에 다른 탭으로 옮겨도 시작한 탭에 들어간다.
/// 형식 옵션·열 폭·비교 기준 고정은 앱 전체가 함께 쓴다(<see cref="PlanShared"/>): 탭 A 에서 고정하고 탭 B 의 계획과 비교할 수 있다.
/// SQL 탭이 없을 때(잠금 모니터 등에서 부른 계획)는 탭에 속하지 않는 모델 하나를 쓴다. 작업 공간 저장·복원에는 계획을 넣지 않는다(결과와 같다).
/// </summary>
[OrandaToolPanel("oranda.panel.plan", ToolPanelKind.Tool, DockLocation.Bottom, Order = 30)]
public sealed partial class PlanPanelViewModel : ToolPanelViewModelBase, IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly Func<PlanShared, PlanViewModel> _factory;
    private readonly Dictionary<Guid, PlanViewModel> _byTab = [];
    private PlanViewModel? _fallback;
    private SqlEditorHost? _host;
    private ICommandRegistry? _registry;
    private ContextKeyService? _keys;
    private Oranda.Core.Features.IFeatureGate? _gate;

    /// <summary>지금 열려 있는 도구 창(명령 처리기가 계획을 넣을 모델을 찾는 곳). 없으면 null.</summary>
    public static PlanPanelViewModel? Instance { get; private set; }

    // 생성자는 IServiceProvider 하나만 둔다(ResultsPanelViewModel 과 같은 이유: 컨테이너가 비공개 생성자의 매개변수 형식까지 살핀다).
    public PlanPanelViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Shared = new PlanShared();
        if (services.GetService(typeof(Oranda.Core.Storage.IAppDataPaths)) is Oranda.Core.Storage.IAppDataPaths paths)
        {
            Shared = new PlanShared { Columns = new PlanColumnWidths(paths.DataDirectory) };
        }

        _factory = shared => new PlanViewModel(services, shared);
        Subscribe(services);
        Attach(SqlEditorHost.Current); // 창은 처음 보일 때 만들어진다: 그 전에 열린 탭도 바로 따라간다
        Instance = this;
    }

    private PlanPanelViewModel(Func<PlanShared, PlanViewModel> factory)
    {
        Shared = new PlanShared();
        _factory = factory;
    }

    /// <summary>시험용: 지정한 편집기 호스트를 따라가고, 탭마다 <paramref name="plans"/> 로 계획을 만드는 모델을 쓴다.</summary>
    internal static PlanPanelViewModel ForHost(SqlEditorHost host, IExecutionPlanService plans, Action<Action>? postToUi = null)
    {
        var panel = new PlanPanelViewModel(shared => new PlanViewModel(shared, plans) { PostToUi = postToUi ?? (a => a()) });
        panel.Attach(host);
        Instance = panel;
        return panel;
    }

    /// <summary>앱 전체가 함께 쓰는 값(형식 옵션·열 폭·비교 기준 고정).</summary>
    public PlanShared Shared { get; }

    /// <summary>지금 보이는 계획 모델. 활성 SQL 탭의 것이고, SQL 탭이 없으면 탭에 속하지 않는 것이다.</summary>
    [ObservableProperty]
    private PlanViewModel? _active;

    /// <summary>
    /// 계획 모델을 새로 만들 때마다 부른다(모델, 그 탭 — 탭에 속하지 않는 모델이면 null). 명령 처리기가 다시 실행·설정 연결을 한 번 잇는 데 쓴다:
    /// 잠금 모니터·자식 커서처럼 계획 명령을 거치지 않고 모델에 바로 넣는 길에서도 연결이 빠지지 않는다.
    /// </summary>
    public static Action<PlanViewModel, SqlEditorViewModel?>? ModelCreated { get; set; }

    /// <summary>탭의 계획 모델(없으면 만든다). <paramref name="tab"/> 이 null 이면 탭에 속하지 않는 모델.</summary>
    public PlanViewModel For(SqlEditorViewModel? tab)
    {
        if (tab is null)
        {
            if (_fallback is null)
            {
                _fallback = _factory(Shared);
                ModelCreated?.Invoke(_fallback, null);
            }

            return _fallback;
        }

        if (!_byTab.TryGetValue(tab.TabId, out var vm))
        {
            vm = _factory(Shared);
            _byTab[tab.TabId] = vm;
            ModelCreated?.Invoke(vm, tab);
        }

        vm.TabLabel = tab.Title;
        return vm;
    }

    /// <summary>이 탭의 계획 모델이 이미 있는가(닫힌 탭의 모델을 되살리지 않고 확인할 때).</summary>
    public bool Has(Guid tabId) => _byTab.ContainsKey(tabId);

    public void Dispose()
    {
        Attach(null);
        if (_registry is not null)
        {
            _registry.CommandsChanged -= OnEnvironmentChanged;
        }

        if (_keys is not null)
        {
            _keys.Changed -= OnEnvironmentChanged;
        }

        if (_gate is not null)
        {
            _gate.Changed -= OnEnvironmentChanged;
        }

        foreach (var vm in _byTab.Values)
        {
            vm.Dispose();
        }

        _byTab.Clear();
        _fallback?.Dispose();
        _fallback = null;
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void Subscribe(IServiceProvider services)
    {
        // 「통계 보기」「자식 커서」 명령은 다른 기능이 등록한다. 명령 목록·문맥 키·기능 게이트가 바뀐 소식은 여기서 한 번만 받아 탭마다 알린다.
        _registry = services.GetService(typeof(ICommandRegistry)) as ICommandRegistry;
        _keys = services.GetService(typeof(ContextKeyService)) as ContextKeyService ?? ContextKeyService.Shared;
        _gate = services.GetService(typeof(Oranda.Core.Features.IFeatureGate)) as Oranda.Core.Features.IFeatureGate;
        if (_registry is not null)
        {
            _registry.CommandsChanged += OnEnvironmentChanged;
        }

        if (_keys is not null)
        {
            _keys.Changed += OnEnvironmentChanged;
        }

        if (_gate is not null)
        {
            _gate.Changed += OnEnvironmentChanged;
        }
    }

    private void OnEnvironmentChanged(object? sender, EventArgs e) => Active?.NotifyEnvironmentChanged();

    private void Attach(SqlEditorHost? host)
    {
        if (_host is not null)
        {
            _host.ActiveChanged -= OnActiveChanged;
            _host.TabClosed -= OnTabClosed;
        }

        _host = host;
        if (_host is not null)
        {
            _host.ActiveChanged += OnActiveChanged;
            _host.TabClosed += OnTabClosed;
        }

        Follow(_host?.Active);
    }

    private void OnActiveChanged(object? sender, EventArgs e) => Follow(_host?.Active);

    private void Follow(SqlEditorViewModel? tab)
    {
        Active = For(tab);
        PlanViewModel.SetCurrent(Active);
        Active.RefreshFeatures(); // 보이지 않던 사이 바뀐 명령·기능 게이트·실행 단추 활성 상태를 맞춘다
    }

    /// <summary>탭이 닫혔다: 그 탭의 계획을 해제한다(비교 기준으로 고정한 계획은 사본이라 남는다).</summary>
    private void OnTabClosed(object? sender, SqlEditorViewModel tab)
    {
        if (_byTab.Remove(tab.TabId, out var vm))
        {
            var wasActive = ReferenceEquals(Active, vm);
            vm.Dispose();
            if (wasActive)
            {
                Follow(_host?.Active is { } now && now.TabId != tab.TabId ? now : null);
            }
        }
    }
}
