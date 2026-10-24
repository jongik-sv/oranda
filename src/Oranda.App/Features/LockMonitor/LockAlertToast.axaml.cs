using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// 임계치 알림 카드(시안 d). Lock 모니터 탭 안 오른쪽 아래에 뜨고, 탭이 뒤에 있으면(설정 「창이 뒤에 있어도」) 메인 창 오버레이에 같은 카드를 띄운다.
/// 운영체제 알림은 쓰지 않는다(새 의존 없음).
/// </summary>
public partial class LockAlertToast : UserControl
{
    public LockAlertToast()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => (DataContext as LockMonitorViewModel)?.DismissAlert();
        ShowTreeButton.Click += (_, _) =>
        {
            if (DataContext is LockMonitorViewModel vm)
            {
                _ = vm.ShowAlertHolderAsync();
            }
        };
        OpenSnapshotButton.Click += (_, _) => _ = OpenSnapshotAsync();
    }

    private async Task OpenSnapshotAsync()
    {
        if (DataContext is LockMonitorViewModel { ActiveAlert.SnapshotPath: { } path } && File.Exists(path)
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }

    /// <summary>메인 창 오버레이 층에 카드를 오른쪽 아래(상태 줄 위)로 띄운다. 돌려준 함수를 부르면 내린다.</summary>
    internal static Action ShowFloating(TopLevel top, LockMonitorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(vm);
        if (OverlayLayer.GetOverlayLayer(top) is not { } layer)
        {
            return () => { };
        }

        var toast = new LockAlertToast { DataContext = vm };
        void Place()
        {
            toast.Measure(layer.Bounds.Size);
            Canvas.SetLeft(toast, Math.Max(8, layer.Bounds.Width - toast.DesiredSize.Width - 16));
            Canvas.SetTop(toast, Math.Max(8, layer.Bounds.Height - toast.DesiredSize.Height - 40));
        }

        void OnBounds(object? s, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Avalonia.Visual.BoundsProperty)
            {
                Place();
            }
        }

        layer.Children.Add(toast);
        Place();
        layer.PropertyChanged += OnBounds;
        return () =>
        {
            layer.PropertyChanged -= OnBounds;
            layer.Children.Remove(toast);
        };
    }
}
