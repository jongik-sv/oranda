using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>
/// 팩터리가 없는 단계의 「준비 중」 자리 표시(시험용·중간 머지용). 늘 끝남이라 [다음]으로 넘어갈 수 있다.
/// </summary>
public sealed class AiSetupPlaceholderStep : IAiSetupStep
{
    private readonly bool _english;
    private Control? _view;

    public AiSetupPlaceholderStep(AiSetupStepId stepId, bool english)
    {
        StepId = stepId;
        _english = english;
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public AiSetupStepId StepId { get; }

    public string Title => AiSetupText.StepTitle(StepId, _english);

    public bool IsComplete => true;

    public Control View => _view ??= new StackPanel
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = AiSetupWizardText.PlaceholderHeadline(_english), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = AiSetupWizardText.PlaceholderDetail(_english), TextWrapping = TextWrapping.Wrap, Classes = { "panel-caption" } },
        },
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public Task EnterAsync(CancellationToken ct) => Task.CompletedTask;

    public Task LeaveAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
