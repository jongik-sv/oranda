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
        CanFloat = panel is not IDockTabActions; // SQL 작업 공간은 떼어 내지 않는다(두 번 누르기 = 이름 바꾸기)
    }

    public ToolPanelDefinition Definition { get; }

    public object Panel { get; }

    /// <summary>
    /// 미리보기 탭(VS Code 방식, 탭 머리글이 기울임꼴)이면 true. 문서 영역에 하나뿐이고, 다음 미리보기가 이 탭 자리를 차지한다.
    /// 고정하면(<see cref="DockWorkspace.Pin"/>) false 가 되어 그대로 남는다. 미리보기 동안은 떼어 내지 않는다(<c>CanFloat</c> 끔) —
    /// Dock 은 탭 머리글을 두 번 누르면 창으로 떼어 내는데, 미리보기 탭에서는 그 동작이 「고정」이어야 해서다.
    /// </summary>
    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            if (SetProperty(ref _isPreview, value))
            {
                CanFloat = !value && Panel is not IDockTabActions;
            }
        }
    }

    private bool _isPreview;
}
