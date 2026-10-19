using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Oranda.App.Features.Localization;

/// <summary>
/// 한/영 화면 글자 표. 정본은 <c>Resources/strings-ko.json</c>·<c>strings-en.json</c>(키 → 글자, 임베디드 리소스)와 <c>Shell/Resources/CommandTitles*.json</c>(명령 제목)이고
/// 언어마다 처음 쓸 때 한 번 읽는다. 두 파일은 키 집합이 같아야 한다(시험이 누락을 잡는다).
/// </summary>
public static class StringCatalog
{
    public static readonly IReadOnlyList<string> Languages = ["ko", "en"];

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.Ordinal);

    /// <summary>언어의 전체 표. 알 수 없는 언어는 빈 표.</summary>
    public static IReadOnlyDictionary<string, string> Load(string language) =>
        Cache.GetOrAdd(language, Read);

    /// <summary>키의 글자. 없으면 null.</summary>
    public static string? Get(string language, string key) =>
        Load(language).TryGetValue(key, out var text) ? text : null;

    private static IReadOnlyDictionary<string, string> Read(string language)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        // 셸·메뉴·대화상자 글자, 그리고 명령 제목(Cmd.<ID>)은 파일이 따로다. 명령 제목 한국어 표(CommandTitles.json)는
        // 맥 쪽이 만든 것을 그대로 쓰고, 영어 표(CommandTitles-en.json)는 같은 키로 짝을 맞춘다.
        Merge(table, $"Oranda.App.strings-{language}.json");
        Merge(table, language == "ko" ? "Oranda.App.Resources.CommandTitles.json" : $"Oranda.App.Resources.CommandTitles-{language}.json");
        return table;
    }

    private static void Merge(Dictionary<string, string> table, string resourceName)
    {
        using var stream = typeof(StringCatalog).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return;
        }

        using var doc = JsonDocument.Parse(stream);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.GetString() is { } text)
            {
                table[p.Name] = text;
            }
        }
    }
}
