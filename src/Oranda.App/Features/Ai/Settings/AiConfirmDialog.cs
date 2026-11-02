using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>프로필 삭제·변경 버림을 묻는 작은 확인 창. 확인이면 true, 취소·닫기면 false.</summary>
internal static class AiConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string message, string okText, string cancelText)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var ok = new Button { Content = okText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = cancelText, IsCancel = true };
        var window = new Window
        {
            Title = message.Length > 40 ? string.Empty : message,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        return window.ShowDialog<bool>(owner);
    }
}
