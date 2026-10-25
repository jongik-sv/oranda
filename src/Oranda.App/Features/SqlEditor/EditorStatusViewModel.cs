using System.ComponentModel;
using System.Globalization;
using Oranda.App.Features.Localization;
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
    private EventHandler? _languageHandler;

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
            var text = UiText.Format("Editor.status.position", "줄 {0}, 열 {1}", p.Line, p.Column);
            if (tab.SelectionLength > 0)
            {
                text += UiText.Format("Editor.status.selection", " (선택 {0}자)", tab.SelectionLength);
            }

            if (tab.CursorCount > 1)
            {
                text += UiText.Format("Editor.status.cursors", " · 커서 {0}개", tab.CursorCount);
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
        _languageHandler = (_, _) => OnPropertyChanged(nameof(Text)); // 언어가 바뀌면 글자를 다시 만든다
        UiText.WatchLanguage(_languageHandler);
        Host.ActiveChanged += OnActiveChanged;
        Watch(Host.Active);
    }

    public void Dispose()
    {
        if (_attached)
        {
            Host.ActiveChanged -= OnActiveChanged;
            if (_languageHandler is not null)
            {
                UiText.UnwatchLanguage(_languageHandler);
            }
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
