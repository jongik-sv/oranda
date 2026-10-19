using System;
using System.Collections.Generic;
using Oranda.App.Features.Localization;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 리소스 키(<c>Cmd.&lt;ID&gt;</c>, <c>Panel.&lt;ID&gt;</c>, <c>Menu.*</c>, <c>Shell.*</c>, <c>Cat.*</c>) → 화면 글자.
/// 글자는 한/영 리소스 파일(<c>Resources/strings-ko.json</c>, <c>strings-en.json</c>)에 있고 설정의 언어에 따라 고른다(TSK-10-02).
/// 리소스에 없는 키는 키에서 사람이 읽을 수 있는 이름을 만들어 쓴다(명령 ID 마지막 마디를 띄어 쓴 것).
/// </summary>
public interface IShellText
{
    /// <summary>지금 쓰는 언어(<c>ko</c> | <c>en</c>).</summary>
    string Language { get; }

    /// <summary>언어가 바뀌었다. 메뉴·도구 창 제목처럼 이미 만든 글자는 이 알림으로 다시 만든다.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>언어를 바꾼다(<c>ko</c> | <c>en</c>, 그 밖의 값은 무시). 실제로 바뀌면 <see cref="LanguageChanged"/>.</summary>
    void SetLanguage(string language);

    /// <summary>리소스 키의 글자. 모르면 <paramref name="key"/> 에서 만든 대체 글자.</summary>
    string Lookup(string key);

    /// <summary>리소스 키의 글자. 리소스에 없으면 null(대체 글자를 만들지 않는다).</summary>
    string? TryLookup(string key);

    /// <summary>지금 언어가 아니라 <paramref name="language"/> 로 본 글자(언어를 바꿀 때 옛 제목을 알아보는 데 쓴다).</summary>
    string LookupIn(string language, string key);

    string Title(CommandDefinition command);

    string Title(ToolPanelDefinition panel);

    string CategoryName(CommandCategory category);
}

/// <inheritdoc />
public sealed class ShellText : IShellText
{
    private readonly Func<string, string?>? _resources;
    private string _language;

    /// <param name="resources">추가 리소스 조회(확장 등). 없거나 null 을 돌려주면 내장 한/영 리소스를 쓴다.</param>
    /// <param name="language">처음 언어. 기본은 한국어.</param>
    public ShellText(Func<string, string?>? resources = null, string language = "ko")
    {
        _resources = resources;
        _language = Normalize(language);
    }

    public event EventHandler? LanguageChanged;

    public string Language => _language;

    public void SetLanguage(string language)
    {
        var next = Normalize(language);
        if (next == _language)
        {
            return;
        }

        _language = next;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Lookup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryLookup(key) ?? (IsResourceKey(key) ? Fallback(key) : key); // 특성에 화면 글자를 바로 적은 제목(예: 「새 PL/SQL 편집기」)은 그대로 쓴다.
    }

    public string? TryLookup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _resources?.Invoke(key) is { } found ? found
            : StringCatalog.Get(_language, key);
    }

    public string LookupIn(string language, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return StringCatalog.Get(Normalize(language), key) ?? Fallback(key);
    }

    /// <summary>
    /// 명령 제목. 명령 ID 로 만든 리소스 키(<c>Cmd.&lt;ID&gt;</c>)가 있으면 그것이 먼저다 —
    /// 특성에 한국어 제목을 직접 적은 명령도 언어를 따라 바뀐다.
    /// </summary>
    public string Title(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TryLookup("Cmd." + command.Id) ?? Lookup(command.Title);
    }

    public string Title(ToolPanelDefinition panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return TryLookup("Panel." + panel.Id) ?? Lookup(panel.Title);
    }

    public string CategoryName(CommandCategory category) => TryLookup("Cat." + category) ?? category.ToString();

    private static bool IsResourceKey(string key) =>
        key.StartsWith("Cmd.", StringComparison.Ordinal) || key.StartsWith("Panel.", StringComparison.Ordinal)
        || key.StartsWith("Menu.", StringComparison.Ordinal) || key.StartsWith("Shell.", StringComparison.Ordinal)
        || key.StartsWith("Cat.", StringComparison.Ordinal) || key.StartsWith("oranda.", StringComparison.Ordinal);

    private static string Normalize(string? language) => language == "en" ? "en" : "ko";


    /// <summary><c>Cmd.oranda.exec.runStatement</c> → <c>exec run Statement</c> 처럼 읽을 수 있는 이름.</summary>
    internal static string Fallback(string key)
    {
        var id = key;
        foreach (var prefix in new[] { "Cmd.", "Panel.", "Menu." })
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                id = id[prefix.Length..];
                break;
            }
        }

        if (id.StartsWith("oranda.", StringComparison.Ordinal))
        {
            id = id["oranda.".Length..];
        }

        var parts = id.Split('.');
        var last = parts[^1];
        var words = new System.Text.StringBuilder();
        foreach (var ch in last)
        {
            if (char.IsUpper(ch) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(ch));
        }

        return parts.Length > 1 ? $"{parts[^2]} {words}" : words.ToString();
    }
}
