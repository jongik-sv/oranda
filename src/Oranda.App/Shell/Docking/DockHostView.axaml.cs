using System;
using Avalonia.Controls;

namespace Oranda.App.Shell.Docking;

/// <summary>도킹 영역(메뉴·툴바·상태 표시줄 사이). <see cref="DockWorkspace"/> 를 DataContext 로 받는다.</summary>
public partial class DockHostView : UserControl
{
    private DockWorkspace? _workspace;

    public DockHostView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DockWorkspace);
    }

    /// <summary>안쪽 Dock 컨트롤(시험이 트리를 살필 때 쓴다).</summary>
    internal Dock.Avalonia.Controls.DockControl DockControl => DockRoot;

    private void Attach(DockWorkspace? workspace)
    {
        if (_workspace is not null)
        {
            _workspace.LayoutRebuilt -= OnRebuilt;
        }

        _workspace = workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.LayoutRebuilt += OnRebuilt;
        OnRebuilt(workspace, EventArgs.Empty);
    }

    private void OnRebuilt(object? sender, EventArgs e)
    {
        DockRoot.Factory = _workspace!.Factory;
        DockRoot.Layout = _workspace.Layout;
    }
}
