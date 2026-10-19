using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Palette;

/// <summary>팔레트 목록의 한 줄.</summary>
public sealed class PaletteItem(CommandDefinition definition, string title, string category, string? shortcut, bool isEnabled)
{
    public CommandDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Title { get; } = title;

    public string Category { get; } = category;

    /// <summary>「범주: 제목」(commands.md 1장 표시 이름).</summary>
    public string Display => $"{Category}: {Title}";

    public string? Shortcut { get; } = shortcut;

    /// <summary>화면용 단축키 글(macOS 는 ⌘ 기호).</summary>
    public string? ShortcutDisplay => Keymaps.KeyBindingTable.ForScreen(Shortcut);

    /// <summary>false 면 회색이고 실행할 수 없다(활성 조건이 거짓).</summary>
    public bool IsEnabled { get; } = isEnabled;
}

/// <summary>
/// 명령 팔레트(<c>Ctrl+Shift+P</c>): 레지스트리의 <c>ShowInPalette</c> 명령 전체를 이름·범주·ID·별칭으로 검색하고 실행한다.
/// 최근에 실행한 명령이 먼저 나온다.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly ICommandRegistry _registry;
    private readonly IContextKeys _keys;
    private readonly IShellText _text;
    private readonly Func<string, string?> _shortcut;
    private readonly IReadOnlyList<string> _recent;
    private readonly Action<string>? _onExecuted;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selected;

    public CommandPaletteViewModel(
        ICommandRegistry registry,
        IContextKeys keys,
        IShellText text,
        Func<string, string?> shortcut,
        IReadOnlyList<string>? recent = null,
        Action<string>? onExecuted = null)
    {
        _registry = registry;
        _keys = keys;
        _text = text;
        _shortcut = shortcut;
        _recent = recent ?? [];
        _onExecuted = onExecuted;
        Refresh();
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];

    /// <summary>실행 뒤 또는 취소 때 창을 닫아 달라는 요청.</summary>
    public event EventHandler? CloseRequested;

    public string WatermarkText => _text.Lookup("Shell.palette.watermark");

    public string EmptyText => _text.Lookup("Shell.palette.none");

    public bool HasResults => Results.Count > 0;

    /// <summary>검색 결과(팔레트에 보일 순서)를 계산한다.</summary>
    public IReadOnlyList<PaletteItem> Search(string query)
    {
        var tokens = (query ?? string.Empty).Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var recent = _recent.ToList();
        var scored = new List<(PaletteItem Item, int Score, int Recent)>();
        foreach (var def in _registry.Commands.Where(c => c.ShowInPalette))
        {
            var item = new PaletteItem(def, _text.Title(def), _text.CategoryName(def.Category), _shortcut(def.Id), _registry.CanExecute(def.Id, _keys));
            var score = Score(item, tokens);
            if (score >= 0)
            {
                var recentIndex = recent.IndexOf(def.Id);
                scored.Add((item, score, recentIndex < 0 ? int.MaxValue : recentIndex));
            }
        }

        return
        [
            .. scored
                .OrderBy(s => tokens.Length == 0 ? 0 : s.Score)
                .ThenBy(s => s.Recent)
                .ThenByDescending(s => s.Item.IsEnabled)
                .ThenBy(s => s.Item.Category, StringComparer.CurrentCulture)
                .ThenBy(s => s.Item.Title, StringComparer.CurrentCulture)
                .ThenBy(s => s.Item.Id, StringComparer.Ordinal)
                .Select(s => s.Item),
        ];
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    /// <summary>선택한 명령을 실행한다. 비활성이거나 선택이 없으면 false(창은 그대로).</summary>
    public async Task<bool> ExecuteSelectedAsync()
    {
        if (Selected is not { IsEnabled: true } item)
        {
            return false;
        }

        // 명령이 대화상자를 열 수 있으므로 팔레트를 먼저 닫아 포커스를 돌려준다.
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            var ran = await _registry.ExecuteAsync(item.Id, _keys).ConfigureAwait(true);
            if (ran)
            {
                _onExecuted?.Invoke(item.Id);
            }

            return ran;
        }
        catch (Exception ex)
        {
            Trace.TraceError("팔레트 명령 '{0}' 실행 중 예외: {1}", item.Id, ex);
            return false;
        }
    }

    public void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        var results = Search(Query);
        Results.Clear();
        foreach (var r in results)
        {
            Results.Add(r);
        }

        Selected = Results.FirstOrDefault(r => r.IsEnabled) ?? Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>모든 토큰이 맞으면 점수(작을수록 좋음), 하나라도 안 맞으면 -1.</summary>
    private static int Score(PaletteItem item, string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var token in tokens)
        {
            var best = BestFieldScore(item, token);
            if (best < 0)
            {
                return -1;
            }

            total += best;
        }

        return total;
    }

    private static int BestFieldScore(PaletteItem item, string token)
    {
        const StringComparison c = StringComparison.CurrentCultureIgnoreCase;
        if (item.Title.StartsWith(token, c))
        {
            return 0;
        }

        if (item.Title.Contains(token, c))
        {
            return 1;
        }

        if (item.Category.Contains(token, c))
        {
            return 2;
        }

        if (item.Id.Contains(token, c))
        {
            return 3;
        }

        if (item.Definition.Aliases.Any(a => a.Contains(token, c)))
        {
            return 4;
        }

        if (IsSubsequence(token, item.Title) || IsSubsequence(token, item.Id))
        {
            return 5;
        }

        return -1;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var ch in haystack)
        {
            if (i < needle.Length && char.ToUpperInvariant(ch) == char.ToUpperInvariant(needle[i]))
            {
                i++;
            }
        }

        return i == needle.Length;
    }
}
