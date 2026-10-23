using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Connection;
using Oranda.Core.Shell;

namespace Oranda.App.ViewModels;

/// <summary>
/// 메인 창의 모델: 도킹, 상태 표시줄 구간, 창 제목, 환경 테두리. 메뉴·툴바는 레지스트리에서 View 쪽이 만든다.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ShellController _shell;

    [ObservableProperty]
    private ConnectionEnvironment? _environment;

    public MainViewModel(ShellController shell, IStatusBarRegistry statusBar, ServiceContainer services)
    {
        _shell = shell;
        Docking = shell.Docking;
        StatusItems = [.. statusBar.Items.Select(i => new StatusBarEntry(i, (IStatusBarItem)services.Create(i.ItemType)))];
        _environment = shell.ActiveEnvironment;
        shell.EnvironmentChanged += (_, _) =>
        {
            Environment = shell.ActiveEnvironment;
            OnPropertyChanged(nameof(Title)); // 운영 접속 열림(ProductionOpen)만 바뀌었을 수 있다
        };
        Docking.ActiveDocumentChanged += (_, _) => FollowActiveDocument();
        FollowActiveDocument();
    }

    private PanelDocument? _document;
    private IInnerTabsDocument? _inner;

    public DockWorkspace Docking { get; }

    public IReadOnlyList<StatusBarEntry> StatusItems { get; }

    /// <summary>
    /// 창 제목. 운영 접속이 활성이거나 하나라도 열려 있으면 <c>[운영]</c> 을 붙인다(ui-shell.md 4.2). 활성 문서가 있으면 오렌지처럼
    /// <c>- [문서 / 안쪽 탭]</c>(예: <c>Oranda - [기본 / SQL 1]</c>)을 붙여 지금 어느 작업 공간·탭인지 보인다.
    /// </summary>
    public string Title
    {
        get
        {
            var title = Environment == ConnectionEnvironment.Production || _shell.ProductionOpen
                ? $"{_shell.Text.Lookup("Shell.window.title")} [운영]"
                : _shell.Text.Lookup("Shell.window.title");
            if (_document is not { } doc)
            {
                return title;
            }

            var inner = _inner?.ActiveInnerTitle is { Length: > 0 } t ? " / " + t : string.Empty;
            return $"{title} - [{doc.Title}{inner}]";
        }
    }

    /// <summary>활성 문서(와 그 안쪽 탭)의 이름이 바뀌면 창 제목을 다시 알린다.</summary>
    private void FollowActiveDocument()
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
        }

        if (_inner is not null)
        {
            _inner.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _document = Docking.ActiveDocument;
        _inner = _document?.Panel as IInnerTabsDocument;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
        }

        if (_inner is not null)
        {
            _inner.PropertyChanged += OnDocumentPropertyChanged;
        }

        OnPropertyChanged(nameof(Title));
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelDocument.Title) or nameof(IInnerTabsDocument.ActiveInnerTitle))
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>테두리 스타일 클래스(<c>env-production</c> 등). 접속 없음이면 null.</summary>
    public string? EnvironmentClass => Environment switch
    {
        ConnectionEnvironment.Production => "env-production",
        ConnectionEnvironment.Test => "env-test",
        ConnectionEnvironment.Development => "env-development",
        _ => null,
    };

    partial void OnEnvironmentChanged(ConnectionEnvironment? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EnvironmentClass));
    }
}

/// <summary>
/// 상태 표시줄 구간 하나(등록 정보 + 만들어진 ViewModel). 구간이 보일지는 구간 자신의 <c>IsVisible</c> 과
/// 좁을 때 접기(<see cref="Collapsed"/>)를 합쳐 <see cref="IsShown"/> 으로 알린다.
/// </summary>
public sealed partial class StatusBarEntry : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private bool _collapsed;

    public StatusBarEntry(StatusBarItemDefinition definition, IStatusBarItem item)
    {
        Definition = definition;
        Item = item;
        if (item is System.ComponentModel.INotifyPropertyChanged changed)
        {
            changed.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is null or nameof(IStatusBarItem.IsVisible))
                {
                    OnPropertyChanged(nameof(IsShown));
                }
            };
        }
    }

    public StatusBarItemDefinition Definition { get; }

    public IStatusBarItem Item { get; }

    public bool IsShown => !Collapsed && Item.IsVisible;

    /// <summary>구간이 INotifyPropertyChanged 를 지원하지 않을 때 다시 읽게 한다.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsShown));
}
