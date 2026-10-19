using System.ComponentModel;
using System.Globalization;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SqlEditor;

/// <summary>상태 표시줄 5번 구간(편집기): 줄·열, 선택 길이, 커서 개수(다중 커서일 때). ui-shell.md 1.2.</summary>
[OrandaStatusBarItem("oranda.status.editor", 5, CollapseRank = 2)]
public sealed class EditorStatusViewModel : ViewModelBase, IStatusBarItem, IDisposable
{
    private SqlEditorHost? _host;
    private SqlEditorViewModel? _watched;
    private bool _attached;

    // 매개변수 없는 생성자만 둔다(이유는 OutlineViewModel 참고). 호스트에는 처음 읽을 때 붙는다.

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public bool IsVisible
    {
        get
        {
            EnsureAttached();
            return _watched is not null;
        }
    }

    public string Text
    {
        get
        {
            EnsureAttached();
            if (_watched is not { } tab)
            {
                return string.Empty;
            }

            var p = tab.CaretPosition;
            var text = $"줄 {p.Line.ToString(CultureInfo.InvariantCulture)}, 열 {p.Column.ToString(CultureInfo.InvariantCulture)}";
            if (tab.SelectionLength > 0)
            {
                text += $" (선택 {tab.SelectionLength.ToString(CultureInfo.InvariantCulture)}자)";
            }

            if (tab.CursorCount > 1)
            {
                text += $" · 커서 {tab.CursorCount.ToString(CultureInfo.InvariantCulture)}개";
            }

            return text;
        }
    }

    private void EnsureAttached()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Host.ActiveChanged += OnActiveChanged;
        Watch(Host.Active);
    }

    public void Dispose()
    {
        if (_attached)
        {
            Host.ActiveChanged -= OnActiveChanged;
        }

        Watch(null);
    }

    private void OnActiveChanged(object? sender, EventArgs e) => Watch(Host.Active);

    private void Watch(SqlEditorViewModel? tab)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnTabChanged;
        }

        _watched = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnTabChanged;
        }

        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Text));
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SqlEditorViewModel.CaretOffset) or nameof(SqlEditorViewModel.SelectionLength))
        {
            OnPropertyChanged(nameof(Text));
        }
    }
}
