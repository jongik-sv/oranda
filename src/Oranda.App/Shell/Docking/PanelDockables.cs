using System;
using Dock.Model.Mvvm.Controls;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도킹 도구 창 한 개. 기능 Task 의 ViewModel 을 <see cref="Panel"/> 로 감싼다.
/// ViewModel 은 처음 <see cref="Panel"/> 을 읽을 때(= 처음 화면에 보일 때) 만든다(지연 생성, D-006).
/// </summary>
public sealed class PanelTool : Tool
{
    private readonly Func<object> _create;
    private object? _panel;

    public PanelTool(ToolPanelDefinition definition, string title, Func<object> create)
    {
        Definition = definition;
        _create = create;
        Id = definition.Id;
        Title = title;
        CanFloat = true;
    }

    public ToolPanelDefinition Definition { get; }

    /// <summary>ViewModel 이 이미 만들어졌는지(시험·진단용).</summary>
    public bool IsPanelCreated => _panel is not null;

    /// <summary>도구 창 ViewModel(<see cref="ViewLocator"/> 가 View 를 찾는다).</summary>
    public object Panel => _panel ??= _create();
}

/// <summary>문서 영역 탭 한 개(<see cref="ToolPanelKind.Document"/> 종류).</summary>
public sealed class PanelDocument : Document
{
    public PanelDocument(ToolPanelDefinition definition, string id, string title, object panel)
    {
        Definition = definition;
        Panel = panel;
        Id = id;
        Title = title;
        CanFloat = true;
    }

    public ToolPanelDefinition Definition { get; }

    public object Panel { get; }
}
