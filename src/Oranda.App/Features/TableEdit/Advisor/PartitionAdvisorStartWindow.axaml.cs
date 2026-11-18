using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>AI 파티션 추천 확인 창(모달). DataContext = <see cref="PartitionAdvisorStartViewModel"/>. 받으면 모델이 닫기를 요청한다.</summary>
public partial class PartitionAdvisorStartWindow : Window
{
    public PartitionAdvisorStartWindow()
    {
        InitializeComponent();
        SendButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.SendAsync();
            }
        };
        PreviewButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.TogglePreviewAsync();
            }
        };
        CopyPreviewButton.Click += async (_, _) =>
        {
            if (Vm is { PreviewText.Length: > 0 } vm && Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.PreviewText);
            }
        };
        CancelButton.Click += (_, _) => Vm?.Cancel();
        Closing += (_, _) => Vm?.Abort(); // 진행 중에 창을 닫으면 모으기·AI 를 멈춘다
    }

    private PartitionAdvisorStartViewModel? _wired;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_wired is not null)
        {
            _wired.CloseRequested -= OnCloseRequested;
        }

        _wired = Vm;
        if (_wired is not null)
        {
            _wired.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private PartitionAdvisorStartViewModel? Vm => DataContext as PartitionAdvisorStartViewModel;
}
