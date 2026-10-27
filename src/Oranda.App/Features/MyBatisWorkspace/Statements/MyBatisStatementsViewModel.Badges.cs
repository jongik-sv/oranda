using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

// MyBatisStatementsViewModel 의 일부: 배지 · 보기 모드 · 글자(MyBatisStatementsViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisStatementsViewModel
{
    // ---- 배지 · 보기 모드 --------------------------------------------------------------------

    private void HookProviders()
    {
        _providers = [.. _session!.Workspace.Host.MyBatisHooks.BadgeProviders];
        foreach (var provider in _providers)
        {
            provider.BadgesChanged += OnBadgesChanged;
        }
    }

    private void UnhookProviders()
    {
        foreach (var provider in _providers)
        {
            provider.BadgesChanged -= OnBadgesChanged;
        }

        _providers = [];
    }

    private void OnBadgesChanged(object? sender, EventArgs e)
    {
        foreach (var group in _groups)
        {
            foreach (var node in group.Children)
            {
                RefreshBadges(node);
            }
        }
    }

    // 보기 모드는 세션과 상관없이 한 번 싣고 세션이 바뀌어도 둔다(매퍼 폴더 트리는 MyBatis 문서 없이도 쓴다).
    private void EnsureModes()
    {
        if (_modesLoaded || Host.MyBatisHooks.ViewModes is not { Count: > 0 } modes)
        {
            return;
        }

        _modesLoaded = true;
        foreach (var mode in modes)
        {
            mode.AvailabilityChanged += OnModeAvailabilityChanged;
            _modes.Add(new MyBatisStatementsModeButton(mode, TranslateTitle(mode.Title)) { CheckedChangedCallback = OnModeCheckedChanged });
        }

        UpdateShowsBody();
    }

    private void UpdateShowsBody() => ShowsBody = _hasSession || _modes.Any(m => m.IsAvailable);

    private void ClearModes()
    {
        foreach (var button in _modes)
        {
            button.Mode.AvailabilityChanged -= OnModeAvailabilityChanged;
        }

        _modes.Clear();
        _modesLoaded = false;
        ActiveMode = null;
        IsModeActive = false;
        UpdateShowsBody();
    }

    private void OnModeAvailabilityChanged(object? sender, EventArgs e)
    {
        foreach (var button in _modes.Where(b => ReferenceEquals(b.Mode, sender)).ToList())
        {
            bool was = button.IsAvailable;
            button.IsAvailable = button.Mode.IsAvailable;
            if (!button.IsAvailable && button.IsChecked)
            {
                button.IsChecked = false;
            }
            else if (button.IsAvailable && !was)
            {
                button.IsChecked = true; // 쓸 수 있게 되면(예 매퍼 폴더를 열면) 그 모드로 간다
            }
        }

        UpdateShowsBody();
    }

    private void OnModeCheckedChanged(MyBatisStatementsModeButton button)
    {
        if (_modeSync)
        {
            return;
        }

        if (button.IsChecked)
        {
            _modeSync = true;
            try
            {
                foreach (var other in _modes.Where(m => !ReferenceEquals(m, button)))
                {
                    other.IsChecked = false;
                }
            }
            finally
            {
                _modeSync = false;
            }

            ActiveMode = button;
            IsModeActive = true;
        }
        else if (!_modes.Any(m => m.IsChecked))
        {
            ActiveMode = null;
            IsModeActive = false;
        }
    }

    // ---- 글자 -----------------------------------------------------------------------------------

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var group in _groups)
        {
            group.Title = GroupTitleFor(group.Kind, group.Children.Count);
            foreach (var node in group.Children)
            {
                RefreshNode(node);
            }
        }

        foreach (var button in _modes)
        {
            button.Title = TranslateTitle(button.Mode.Title);
        }

        UpdateHeader();
        RefreshUsage();
        UpdateCounts();
    }

    private string GroupTitle(MyBatisStatementKind kind) => kind switch
    {
        MyBatisStatementKind.Sql => T("group.sql"),
        MyBatisStatementKind.Other => T("group.other"),
        _ => MyBatisStatementEntry.KindName(kind), // select·insert·update·delete·resultMap 은 XML 요소 이름이라 번역하지 않는다
    };

    private string T(string key)
    {
        var full = KeyPrefix + key;
        return ActiveText?.Lookup(full) ?? StringCatalog.Get("ko", full) ?? full;
    }

    /// <summary>보기 모드 이름은 한국어 화면 글로 오므로 영어 화면이면 리터럴 표(literals-en.json)로 바꾼다.</summary>
    private string TranslateTitle(string title) =>
        ActiveText?.Language == "en" && LiteralTranslator.Table.TryGetValue(title, out var english) ? english : title;
}
