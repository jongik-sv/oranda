using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.AskAdmin;

/// <summary>「담당자에게 묻기」 단계 만들기(갈래 C).</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
internal sealed class AskAdminStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.AskAdmin;

    public IAiSetupStep Create(AiSetupContext context) => new AskAdminStep(context);
}

/// <summary>「담당자에게 묻기」 단계: 보낼 글 + 복사. 들어올 때 claude 를 다시 찾아 글에 설치 여부를 넣는다.</summary>
internal sealed class AskAdminStep : IAiSetupStep
{
    private readonly AiSetupContext _context;
    private Control? _view;

    public AskAdminStep(AiSetupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var info = new AiSetupDiagnosticInfo(context.AppVersion, RuntimeInformation.OSDescription, AiSetupPath.NotSure, false, null, null, DateTimeOffset.Now);
        ViewModel = new AskAdminViewModel(info, CopyAsync, context.Text);
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public AskAdminViewModel ViewModel { get; }

    public AiSetupStepId StepId => AiSetupStepId.AskAdmin;

    public string Title => AiSetupText.StepTitle(StepId, _context.IsEnglish);

    public bool IsComplete => ViewModel.IsComplete;

    public Control View => _view ??= new AskAdminView(ViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public async Task EnterAsync(CancellationToken ct)
    {
        if (_context.Claude is not { } claude)
        {
            return;
        }

        try
        {
            var found = await claude.FindAsync(ct).ConfigureAwait(true);
            ViewModel.UpdateClaude(found is not null, found?.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 찾지 못하면 「설치 안 됨」 으로 둔다(글은 그대로 보낼 수 있다).
        }
    }

    public Task LeaveAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Dispose();
    }

    private async Task<bool> CopyAsync(string text)
    {
        await _context.CopyText(text).ConfigureAwait(true);
        return true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskAdminViewModel.IsComplete))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }
}
