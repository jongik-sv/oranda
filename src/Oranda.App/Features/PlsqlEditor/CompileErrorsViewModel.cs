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
    }

    /// <summary>화면에 만들어진 목록 창(편집 탭이 결과를 올려 보낸다). 아직 없으면 null.</summary>
    public static CompileErrorsViewModel? Current { get; private set; }

    public PlsqlEditorViewModel? Source { get; private set; }

    public ObservableCollection<CompileErrorItem> Items { get; } = [];

    [ObservableProperty]
    public partial CompileErrorItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string Header { get; set; } = "컴파일 오류 없음";

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
    }
}
