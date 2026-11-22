using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.Test;

/// <summary>연결 시험 단계 만들기(갈래 A·B 공용).</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
public sealed class AiSetupTestStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Test;

    public IAiSetupStep Create(AiSetupContext context) => new AiSetupTestStep(new AiSetupTestStepViewModel(context));
}

/// <summary>
/// 연결 시험 단계 화면. 들어올 때마다 한 번 시험하고, 실패면 쉬운 원인·할 일·「다시 시험」·「진단 글 복사」 를 보인다.
/// 떠나면 도는 시험을 멈춘다. 화면 조각은 창 틀의 <see cref="AiSetupUi"/>(상태 칸 실패 = 경고색).
/// </summary>
public sealed class AiSetupTestStep : IAiSetupStep
{
    private readonly bool _english;
    private bool _disposed;

    public AiSetupTestStep(AiSetupTestStepViewModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _english = model.English;

        StatusBox = AiSetupUi.Status(nameof(AiSetupTestStepViewModel.StatusKind), nameof(AiSetupTestStepViewModel.Headline), nameof(AiSetupTestStepViewModel.Detail));

        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, MinHeight = 6 };
        bar.Bind(Visual.IsVisibleProperty, new Binding(nameof(AiSetupTestStepViewModel.IsRunning)));

        var diagnostics = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 };
        diagnostics.Bind(TextBlock.TextProperty, new Binding(nameof(AiSetupTestStepViewModel.Diagnostics)));
        var diagnosticsBox = new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6), Child = diagnostics };
        diagnosticsBox.Bind(Border.BackgroundProperty, diagnosticsBox.GetResourceObservable("OrandaCodeBackgroundBrush"));
        diagnosticsBox.Bind(Visual.IsVisibleProperty, new Binding(nameof(AiSetupTestStepViewModel.Diagnostics)) { Converter = StringConverters.IsNotNullOrEmpty });

        RetryButton = AiSetupUi.Button(AiSetupTestText.Retry(_english), accent: true);
        RetryButton.Bind(Visual.IsVisibleProperty, new Binding(nameof(AiSetupTestStepViewModel.IsFailed)));
        RetryButton.Click += async (_, _) => await Model.RunAsync(CancellationToken.None).ConfigureAwait(true);
        CopyButton = AiSetupUi.Button(AiSetupTestText.Copy(_english));
        CopyButton.Bind(Visual.IsVisibleProperty, new Binding(nameof(AiSetupTestStepViewModel.Diagnostics)) { Converter = StringConverters.IsNotNullOrEmpty });
        CopyButton.Click += async (_, _) => await Model.CopyAsync().ConfigureAwait(true);
        var copied = AiSetupUi.Caption(AiSetupTestText.Copied(_english));
        copied.VerticalAlignment = VerticalAlignment.Center;
        copied.Bind(Visual.IsVisibleProperty, new Binding(nameof(AiSetupTestStepViewModel.JustCopied)));

        View = new StackPanel
        {
            Spacing = 12,
            DataContext = Model,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                AiSetupUi.Caption(AiSetupTestText.Intro(_english)),
                StatusBox,
                bar,
                diagnosticsBox,
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 8,
                    LineSpacing = 8,
                    Children = { RetryButton, CopyButton, copied },
                },
            },
        };

        Model.PropertyChanged += OnModelChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public AiSetupTestStepViewModel Model { get; }

    public AiSetupStepId StepId => AiSetupStepId.Test;

    public string Title => AiSetupText.StepTitle(StepId, _english);

    public bool IsComplete => Model.IsComplete;

    public Control View { get; }

    /// <summary>상태 칸(시험이 본다).</summary>
    public Border StatusBox { get; }

    /// <summary>「다시 시험」(실패 때 보임).</summary>
    public Button RetryButton { get; }

    /// <summary>「진단 글 복사」(실패 때 보임).</summary>
    public Button CopyButton { get; }

    public Task EnterAsync(CancellationToken ct)
    {
        // 들어올 때마다 다시 시험한다: 앞 단계(서버 주소·열쇠·로그인 계정)가 바뀌었을 수 있다. 짧은 질문 하나라 싸다.
        // 도는 동안 창 틀을 막지 않게 기다리지 않는다(RunAsync 는 예외를 내지 않는다).
        _ = Model.RunAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task LeaveAsync(CancellationToken ct)
    {
        Model.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Model.Cancel();
        Model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiSetupTestStepViewModel.IsComplete))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }
}
