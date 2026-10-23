using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Shell.Appearance;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridControl 의 일부: 수명·모델(ResultGridControl.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridControl
{
    // ---- 수명·모델 ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.ScalingChanged += OnScalingChanged;
        }

        ActualThemeVariantChanged += OnThemeChanged;
        SubscribeFontResources();
        _palette = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null)
        {
            _top.ScalingChanged -= OnScalingChanged;
            _top = null;
        }

        ActualThemeVariantChanged -= OnThemeChanged;
        UnsubscribeFontResources();
        _cells.Clear();
        _headers.Clear();
        _rowHeads.Clear();
    }

    private void OnScalingChanged(object? sender, EventArgs e) => ResetTextCaches();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _palette = null;
        ResetTextCaches();
    }

    private void ResetTextCaches()
    {
        _factory = null;
        _marker = null;
        _cells.Clear();
        _headers.Clear();
        _rowHeads.Clear();
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadAsync(ResultGridModel m)
    {
        try
        {
            await m.LoadFirstPageAsync().ConfigureAwait(true);
            if (ReferenceEquals(_model, m))
            {
                m.AutoSizeColumns(Factory.CharWidth);
            }
        }
        catch (Exception)
        {
            // 오류는 Model.Error 로 보인다.
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRevealRequested(object? sender, GridCell cell) => EnsureVisible(cell);

    /// <summary>저장된 행 강조 시간. 지나면 강조를 지운다(입력은 그동안에도 막지 않는다).</summary>
    internal static TimeSpan FlashDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    private DispatcherTimer? _flashTimer;

    private void OnFlashStarted(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = FlashDuration };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            _model?.ClearFlash();
        };
        _flashTimer.Start();
    }
}
