using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>컴파일 오류 목록 한 줄.</summary>
public sealed record CompileErrorItem(CompileError Error)
{
    public string Kind => Error.IsWarning ? "경고" : "오류";

    /// <summary>앞 아이콘 키(오류 = 빨간 원, 경고 = 노란 삼각형).</summary>
    public string IconKey => Error.IsWarning ? "warn" : "error";

    public string Position => $"{Error.Line}:{Error.Column}";

    public string Message => Error.Message;
}

/// <summary>
/// <c>oranda.panel.compileErrors</c> — 마지막으로 컴파일한(또는 연) PL/SQL 탭의 오류·경고 목록. 줄을 고르면 그 탭의 그 줄로 간다(TSK-08-01).
/// </summary>
[OrandaToolPanel("oranda.panel.compileErrors", ToolPanelKind.Tool, DockLocation.Bottom, Order = 50)]
public sealed partial class CompileErrorsViewModel : ToolPanelViewModelBase
{
    public CompileErrorsViewModel()
    {
        Current = this;

        // 창을 열기 전에 컴파일한 탭이 있으면 그 결과부터 보여 준다(창은 처음 열 때 만들어진다).
        if (LastSource is { } pending)
        {
            Show(pending);
        }
    }

    /// <summary>화면에 만들어진 목록 창(편집 탭이 결과를 올려 보낸다). 아직 없으면 null.</summary>
    public static CompileErrorsViewModel? Current { get; private set; }

    /// <summary>마지막으로 오류 목록을 올린 탭. 창이 아직 없을 때도 기억해 두었다가 창이 생기면 보여 준다.</summary>
    public static PlsqlEditorViewModel? LastSource { get; private set; }

    /// <summary>편집 탭이 컴파일 결과를 올린다. 창이 있으면 바로 보이고, 없으면 창이 만들어질 때 보인다.</summary>
    public static void Publish(PlsqlEditorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        LastSource = source;
        Current?.Show(source);
    }

    public PlsqlEditorViewModel? Source { get; private set; }

    public ObservableCollection<CompileErrorItem> Items { get; } = [];

    [ObservableProperty]
    public partial CompileErrorItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string Header { get; set; } = "컴파일 오류 없음";

    [ObservableProperty]
    public partial int ErrorCount { get; set; }

    [ObservableProperty]
    public partial int WarningCount { get; set; }

    /// <summary>머리 줄 아이콘: 아직 컴파일한 탭 없음 = 컴파일, 오류 있음 = 오류, 경고만 = 경고, 깨끗함 = 확인.</summary>
    [ObservableProperty]
    public partial string HeaderIconKey { get; set; } = "compile";

    partial void OnSelectedItemChanged(CompileErrorItem? value)
    {
        if (value is not null)
        {
            Source?.NavigateTo(value.Error);
        }
    }

    /// <summary>이 편집 탭의 오류 목록을 보여 준다.</summary>
    public void Show(PlsqlEditorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Source is not null)
        {
            Source.Errors.CollectionChanged -= OnSourceErrorsChanged;
        }

        Source = source;
        source.Errors.CollectionChanged += OnSourceErrorsChanged;
        Refresh();
    }

    private void OnSourceErrorsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Items.Clear();
        SelectedItem = null;
        ErrorCount = 0;
        WarningCount = 0;
        HeaderIconKey = "compile";
        if (Source is null)
        {
            return;
        }

        foreach (var e in Source.Errors)
        {
            Items.Add(new CompileErrorItem(e));
        }

        var errors = 0;
        foreach (var i in Items)
        {
            if (!i.Error.IsWarning)
            {
                errors++;
            }
        }

        Header = Items.Count == 0 ? $"{Source.Title}: 컴파일 오류 없음" : $"{Source.Title}: 오류 {errors}건, 경고 {Items.Count - errors}건";
        ErrorCount = errors;
        WarningCount = Items.Count - errors;
        HeaderIconKey = errors > 0 ? "error" : WarningCount > 0 ? "warn" : "ok";
    }
}
