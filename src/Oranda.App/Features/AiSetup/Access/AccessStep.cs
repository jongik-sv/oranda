using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>「AI 를 쓸 접속」 단계 만들기(갈래 A·B).</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
internal sealed class AccessStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Access;

    public IAiSetupStep Create(AiSetupContext context) => new AccessStep(context);
}

/// <summary>
/// 「AI 를 쓸 접속」 단계. 들어올 때 접속을 읽고, 스위치를 바꿀 때마다 바로 저장한다(저장 실패면 끝나지 않음).
/// 저장된 켠 접속 id 를 <see cref="AiSetupContext.EnabledConnectionIds"/> 에 넣는다(해 보기가 쓴다). 접속 서비스가 없으면 끝남으로 둔다.
/// </summary>
internal sealed class AccessStep : IAiSetupStep
{
    private readonly AiSetupContext _context;
    private Control? _view;
    private readonly bool _noService;
    private readonly SharedAiAccessService? _shared;

    public AccessStep(AiSetupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        // 공유 허용 수준 한 길(설정 「AI」 탭·외부 에이전트 연결 창과 같은 값, ai-settings-one-place). 화면은 그대로다.
        _shared = context.Connections is { } connections ? SharedAiAccessService.FromConnections(connections, context.Bus) : null;
        var services = _shared is { } shared
            ? AccessStepServices.FromShared(shared)
            : new AccessStepServices(_ => Task.FromResult<System.Collections.Generic.IReadOnlyList<Oranda.Core.Connection.ConnectionProfile>>([]), (p, _) => Task.FromResult(p));
        _noService = context.Connections is null;
        ViewModel = new AccessStepViewModel(services, context.Text) { AutoSave = true };
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public AccessStepViewModel ViewModel { get; }

    public AiSetupStepId StepId => AiSetupStepId.Access;

    public string Title => AiSetupText.StepTitle(StepId, _context.IsEnglish);

    public bool IsComplete => ViewModel.IsComplete;

    public Control View => _view ??= new AccessView(ViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public async Task EnterAsync(CancellationToken ct)
    {
        await ViewModel.LoadAsync(ct).ConfigureAwait(true);
        if (_noService || ViewModel.ErrorText.Length == 0)
        {
            await ViewModel.SaveAsync(ct).ConfigureAwait(true); // 바뀐 줄이 없으면 저장 없이 끝남이 된다(켠 목록도 채운다)
        }
    }

    public async Task LeaveAsync(CancellationToken ct) => await ViewModel.LastSave.ConfigureAwait(true);

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Dispose();
        _shared?.Dispose();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccessStepViewModel.EnabledProfiles))
        {
            _context.EnabledConnectionIds = [.. ViewModel.EnabledProfiles.Select(p => p.Id)];
        }

        if (e.PropertyName == nameof(AccessStepViewModel.IsComplete))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }
}
