using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.Server;

/// <summary>「회사 AI 서버」 단계 만들기(갈래 B).</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
internal sealed class ServerStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Server;

    public IAiSetupStep Create(AiSetupContext context) => new ServerStep(context);
}

/// <summary>「회사 AI 서버」 단계: 만든 프로필 id 를 <see cref="AiSetupContext.CreatedProfileId"/> 에 넣는다(연결 시험이 쓴다).</summary>
internal sealed class ServerStep : IAiSetupStep
{
    private readonly AiSetupContext _context;
    private Control? _view;

    public ServerStep(AiSetupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var services = new AiProfileServices(context.Settings, context.Credentials, context.Probe, context.Bus, context.Text);
        ViewModel = new ServerStepViewModel(new ServerProfileSaver(services, context.Text), context.Text);
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public ServerStepViewModel ViewModel { get; }

    public AiSetupStepId StepId => AiSetupStepId.Server;

    public string Title => AiSetupText.StepTitle(StepId, _context.IsEnglish);

    public bool IsComplete => ViewModel.IsComplete;

    public Control View => _view ??= new ServerView(ViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public Task EnterAsync(CancellationToken ct)
    {
        if (!ViewModel.IsComplete)
        {
            ViewModel.Restore(_context.CreatedProfileId, _context.Settings.Current.Ai); // 이어 하기: 앞서 만든 프로필이 있으면 끝난 것으로
        }

        return Task.CompletedTask;
    }

    /// <summary>연결 중에 떠나면 저장이 끝나기를 기다린다(만든 프로필 id 가 이어 하기에 남게).</summary>
    public Task LeaveAsync(CancellationToken ct) => ViewModel.LastConnect;

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Dispose();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerStepViewModel.CreatedProfileId):
                _context.CreatedProfileId = ViewModel.CreatedProfileId;
                break;
            case nameof(ServerStepViewModel.IsComplete):
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
                break;
        }
    }
}
