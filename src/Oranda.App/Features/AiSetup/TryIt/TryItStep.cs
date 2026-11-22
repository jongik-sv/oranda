using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>「해 보기」 단계 만들기(갈래 A·B 마지막).</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
internal sealed class TryItStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.TryIt;

    public IAiSetupStep Create(AiSetupContext context) => new TryItStep(context, context.Services is { } services ? new HostTryItActions(services) : null);
}

/// <summary>
/// 「해 보기」 단계. 들어올 때 <see cref="AiSetupContext.EnabledConnectionIds"/> 의 접속을 읽어 화면을 새로 만든다.
/// 켠 접속이 없으면 건너뜀 안내와 함께 끝남. [끝내기] 때 고급 확인란이 켜져 있으면 외부 에이전트 연결 창을 연다.
/// </summary>
internal sealed class TryItStep : IAiSetupStep
{
    private readonly AiSetupContext _context;
    private readonly ITryItActions? _actions;
    private readonly ContentControl _view = new();
    private TryItViewModel? _vm;
    private bool _entered;

    public TryItStep(AiSetupContext context, ITryItActions? actions)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _actions = actions;
        Swap([]);
    }

    public TryItViewModel ViewModel => _vm!;

    public AiSetupStepId StepId => AiSetupStepId.TryIt;

    public string Title => AiSetupText.StepTitle(StepId, _context.IsEnglish);

    /// <summary>들어와 접속을 읽은 뒤에만 끝남일 수 있다(읽기 전의 빈 목록을 「건너뜀」 으로 보지 않는다).</summary>
    public bool IsComplete => _entered && _vm is { } vm && (vm.IsComplete || vm.IsSkipped);

    public Control View => _view;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public async Task EnterAsync(CancellationToken ct)
    {
        var ids = _context.EnabledConnectionIds;
        if (_entered && _vm is { } current && current.Connections.Select(c => c.Id).ToHashSet().SetEquals(ids))
        {
            return; // 같은 접속이면 연습 탭·확인란 상태를 그대로 둔다
        }

        IReadOnlyList<ConnectionProfile> profiles = [];
        if (ids.Count > 0 && _context.Connections is { } connections)
        {
            try
            {
                var all = (await connections.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
                profiles = [.. ids.Select(id => all.FirstOrDefault(p => p.Id == id)).OfType<ConnectionProfile>()];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                profiles = []; // 읽지 못하면 건너뜀으로 둔다
            }
        }

        _entered = true;
        Swap(profiles, notify: true);
    }

    public Task LeaveAsync(CancellationToken ct) => Task.CompletedTask;

    public Task FinishAsync(CancellationToken ct) => _vm?.FinishAsync(ct) ?? Task.CompletedTask;

    public void Dispose() => Release();

    private void Swap(IReadOnlyList<ConnectionProfile> profiles, bool notify = false)
    {
        Release();
        _vm = new TryItViewModel(_actions ?? NoActions.Instance, profiles, _context.Text);
        _vm.PropertyChanged += OnViewModelChanged;
        _view.Content = new TryItView(_vm);
        if (notify)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }

    private void Release()
    {
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.Dispose();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TryItViewModel.IsComplete))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }

    /// <summary>앱 서비스가 없을 때(조립이 덜 된 시험): 탭을 열지 못한다고 안내한다.</summary>
    private sealed class NoActions : ITryItActions
    {
        public static NoActions Instance { get; } = new();

        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql) => null;

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct) => Task.FromResult(new TryItExplainResult(false, null));

        public Task OpenExternalAgentAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
