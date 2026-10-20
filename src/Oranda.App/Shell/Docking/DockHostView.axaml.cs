using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Oranda.App.Shell.Docking;

/// <summary>도킹 영역(메뉴·툴바·상태 표시줄 사이). <see cref="DockWorkspace"/> 를 DataContext 로 받는다.</summary>
public partial class DockHostView : UserControl
{
    /// <summary>미리보기 탭이면 기울임꼴(탭 머리글 스타일이 쓴다).</summary>
    public static IValueConverter PreviewFontStyle { get; } =
        new FuncValueConverter<bool, FontStyle>(preview => preview ? FontStyle.Italic : FontStyle.Normal);

    private DockWorkspace? _workspace;

    static DockHostView() => DockFloatRedock.Register();

    public DockHostView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DockWorkspace);

        // 미리보기 탭의 머리글을 두 번 누르면 고정한다(VS Code 와 같다).
        DockRoot.AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>안쪽 Dock 컨트롤(시험이 트리를 살필 때 쓴다).</summary>
    internal Dock.Avalonia.Controls.DockControl DockControl => DockRoot;

    private void Attach(DockWorkspace? workspace)
    {
        if (_workspace is not null)
        {
            _workspace.LayoutRebuilt -= OnRebuilt;
            _workspace.DocumentFocusRequested -= OnFocusRequested;
        }

        _workspace = workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.LayoutRebuilt += OnRebuilt;
        workspace.DocumentFocusRequested += OnFocusRequested;
        OnRebuilt(workspace, EventArgs.Empty);
    }

    private void OnRebuilt(object? sender, EventArgs e)
    {
        DockRoot.Factory = _workspace!.Factory;
        DockRoot.Layout = _workspace.Layout;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_workspace is not null && e.Source is Control source
            && source.FindAncestorOfType<DocumentTabStripItem>(includeSelf: true) is { DataContext: PanelDocument { IsPreview: true } doc })
        {
            _workspace.Pin(doc);
        }
    }

    private void OnFocusRequested(object? sender, PanelDocument document) =>
        Dispatcher.UIThread.Post(() => FocusContent(document, 0), DispatcherPriority.Background);

    /// <summary>
    /// 문서 보기에 포커스를 준다. 막 연 탭은 보기가 아직 없을 수 있어 잠깐 뒤 몇 번 더 본다.
    /// 보기가 <see cref="IFocusableDocumentView"/> 면 그 보기가 정한 곳에, 아니면 첫 포커스 가능한 컨트롤에 준다.
    /// </summary>
    private void FocusContent(PanelDocument document, int attempt)
    {
        // DataContext 가 그 ViewModel 인 첫 컨트롤(문서 템플릿의 내용 표시기) 아래에 ViewModel 로 만든 보기가 있다.
        var host = DockRoot.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, document.Panel));
        var focused = host is not null
            && (host.GetSelfAndVisualDescendants().OfType<IFocusableDocumentView>().FirstOrDefault() is { } custom
                ? custom.FocusContent()
                : host.GetVisualDescendants().OfType<InputElement>()
                    .FirstOrDefault(i => i.Focusable && i.IsEffectivelyVisible && i.IsEffectivelyEnabled)?.Focus() ?? false);
        if (!focused && attempt < 20)
        {
            DispatcherTimer.RunOnce(() => FocusContent(document, attempt + 1), TimeSpan.FromMilliseconds(50));
        }
    }
}
