using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.SqlEditor;

/// <summary>상태 표시줄 편집기 구간(<see cref="EditorStatusViewModel"/>)의 View: 「줄 3, 열 12」 한 줄.</summary>
public sealed class EditorStatusView : UserControl
{
    public EditorStatusView()
    {
        Content = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.TextProperty] = new Binding(nameof(EditorStatusViewModel.Text)),
        };
    }
}
