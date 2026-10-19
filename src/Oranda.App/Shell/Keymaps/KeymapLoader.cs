using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 키맵 JSON 로더 틀 (commands.md 4장). 프리셋 4종은 <c>docs/design/keymaps/*.json</c> 을 링크한 임베디드 리소스
/// (<c>Oranda.App.Keymaps.{id}.json</c>, 읽기 전용)다. 사용자 키맵 겹치기·파일 저장은 TSK-04-07.
/// </summary>
public static class KeymapLoader
{
    /// <summary>현재 키맵 파일 버전.</summary>
    public const int CurrentSchemaVersion = SchemaVersions.Keymap;

    private const string ResourcePrefix = "Oranda.App.Keymaps.";

    /// <summary>앱에 든 프리셋 id (commands.md 6장).</summary>
    public static IReadOnlyList<string> PresetIds { get; } = ["default", "orange5", "plsqldev", "sqldev"];

    /// <summary>앱에 든 프리셋을 읽는다. 없는 id 면 <see cref="ArgumentException"/>.</summary>
    public static Keymap LoadPreset(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var stream = typeof(KeymapLoader).Assembly.GetManifestResourceStream(ResourcePrefix + id + ".json")
            ?? throw new ArgumentException($"키맵 프리셋 '{id}' 가 없다.", nameof(id));
        var keymap = Parse(stream);
        if (!string.Equals(keymap.Id, id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"키맵 프리셋 파일 '{id}' 의 id 가 '{keymap.Id}' 다.");
        }

        return keymap;
    }

    /// <summary>
    /// 키맵 JSON 을 읽는다. 형식이 틀리면 <see cref="JsonException"/>, 지원하지 않는 버전이면 <see cref="InvalidDataException"/>.
    /// 옛 버전 상향 변환은 <c>ISettingsMigration</c>(FileKind = <c>keymap</c>)이 이 앞에서 한다.
    /// </summary>
    public static Keymap Parse(Stream json)
    {
        var keymap = JsonSerializer.Deserialize(json, ShellJsonContext.Default.Keymap)
            ?? throw new InvalidDataException("키맵 JSON 이 비어 있다.");
        if (keymap.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"키맵 '{keymap.Id}' 의 schemaVersion {keymap.SchemaVersion} 은 지원하지 않는다.");
        }

        return keymap;
    }

    /// <summary>키맵을 data-model.md 1.3 규칙(camelCase, null 생략, 들여쓰기 2칸)으로 쓴다.</summary>
    public static string Serialize(Keymap keymap) => JsonSerializer.Serialize(keymap, ShellJsonContext.Default.Keymap);
}
