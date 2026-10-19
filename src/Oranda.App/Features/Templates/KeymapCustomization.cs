using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.Templates;

/// <summary>같은 범위(<see cref="When"/>)에서 같은 키를 쓰는 명령이 둘 이상인 경우.</summary>
public sealed record KeymapConflict(string? When, string Keys, IReadOnlyList<string> Commands);

/// <summary>
/// 키맵 겹치기·충돌 검사·바인딩 바꾸기(순수 함수). 사용자 키맵은 <c>basedOn</c> 프리셋 위에 (명령, when) 단위로 덮어쓴다.
/// <c>keys: []</c> 는 바탕 키 지움. 같은 (명령, when) 이 여럿이면 뒤가 이긴다(data-model 2.4).
/// </summary>
public static class KeymapComposer
{
    /// <summary>사용자 키맵을 <paramref name="baseKeymap"/> 위에 겹쳐 실제로 쓸 키맵을 만든다.</summary>
    public static Keymap Compose(Keymap baseKeymap, Keymap? user)
    {
        ArgumentNullException.ThrowIfNull(baseKeymap);
        if (user is null)
        {
            return baseKeymap;
        }

        var result = new List<KeyBinding>(baseKeymap.Bindings);
        foreach (var b in user.Bindings)
        {
            var index = result.FindIndex(x => x.Command == b.Command && x.When == b.When);
            if (index >= 0)
            {
                result[index] = b;
            }
            else
            {
                result.Add(b);
            }
        }

        return new Keymap { SchemaVersion = baseKeymap.SchemaVersion, Id = user.Id, Name = user.Name, BasedOn = user.BasedOn, Bindings = result };
    }

    /// <summary>
    /// 충돌을 찾는다. 비어 있는 키·다른 <c>when</c> 은 충돌이 아니다. 같은 명령이 한 키를 두 번 적은 것도 충돌이 아니다.
    /// 연속 키의 첫 단이 다른 바인딩의 단일 키와 같은 경우도 충돌로 본다(첫 단이 먼저 실행돼 연속 키가 안 된다).
    /// </summary>
    public static IReadOnlyList<KeymapConflict> FindConflicts(Keymap keymap, bool macOS = false)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var entries = new List<(string? When, string Keys, string Command)>();
        foreach (var b in keymap.Bindings)
        {
            foreach (var k in b.KeysFor(macOS))
            {
                entries.Add((b.When, k, b.Command));
            }
        }

        var found = new List<KeymapConflict>();
        foreach (var g in entries.GroupBy(e => (e.When, e.Keys)))
        {
            var commands = g.Select(e => e.Command).Distinct().ToList();
            if (commands.Count > 1)
            {
                found.Add(new KeymapConflict(g.Key.When, g.Key.Keys, commands));
            }
        }

        foreach (var chord in entries.Where(e => e.Keys.Contains(' ', StringComparison.Ordinal)))
        {
            var first = chord.Keys.Split(' ')[0];
            foreach (var single in entries.Where(e => e.When == chord.When && e.Keys == first && e.Command != chord.Command))
            {
                found.Add(new KeymapConflict(chord.When, first, [single.Command, chord.Command]));
            }
        }

        return found;
    }

    /// <summary>
    /// 한 명령의 키를 바꾼 사용자 키맵을 돌려준다. 바탕과 같아지면 그 덮어쓰기는 지운다.
    /// </summary>
    public static Keymap SetKeys(Keymap user, Keymap baseKeymap, string command, string? when, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(baseKeymap);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(keys);
        var list = user.Bindings.Where(b => !(b.Command == command && b.When == when)).ToList();
        var baseline = baseKeymap.Bindings.FirstOrDefault(b => b.Command == command && b.When == when);
        var same = baseline is not null && baseline.Keys.SequenceEqual(keys, StringComparer.Ordinal);
        var emptyBoth = baseline is null && keys.Count == 0;
        if (!same && !emptyBoth)
        {
            list.Add(new KeyBinding { Command = command, Keys = keys, When = when, });
        }

        return user with { Bindings = list };
    }

    /// <summary>키 조합 표기 검증: 정규 순서 수식키 + 아는 키. 틀리면 이유를, 맞으면 null.</summary>
    public static string? Validate(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return "키가 비어 있다.";
        }

        if (!KeyChord.IsNormalized(keys))
        {
            return "수식키 순서는 Primary+Ctrl+Alt+Shift+Meta 이다.";
        }

        try
        {
            KeyChord.Parse(keys, macOS: false);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>사용자 키맵 파일 <c>{data}/keymaps/{id}.json</c> 읽기·쓰기(JsonDocumentFile 소비).</summary>
public sealed class UserKeymapStore(IAppDataPaths paths)
{
    /// <summary>사용자 키맵 기본 id.</summary>
    public const string DefaultId = "my-keys";

    public string PathOf(string id) => Path.Combine(paths.KeymapDirectory, id + ".json");

    public bool Exists(string id) => File.Exists(PathOf(id));

    /// <summary>파일이 없거나 손상이면 null(손상은 백업으로 옮겨진다).</summary>
    public async Task<Keymap?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var result = await JsonDocumentFile.LoadAsync(
            path,
            SettingsFileKinds.Keymap,
            SchemaVersions.Keymap,
            ShellJsonContext.Default.Keymap,
            () => new Keymap { Id = id, Name = id },
            [],
            paths.BackupDirectory,
            ct).ConfigureAwait(false);
        return result.State is JsonLoadState.Corrupt or JsonLoadState.Missing ? null : result.Value;
    }

    public Task SaveAsync(Keymap keymap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        return JsonDocumentFile.WriteAsync(PathOf(keymap.Id), keymap, ShellJsonContext.Default.Keymap, ct);
    }

    public IReadOnlyList<string> ListIds() =>
        Directory.Exists(paths.KeymapDirectory)
            ? Directory.GetFiles(paths.KeymapDirectory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal).ToList()
            : [];
}

/// <summary>
/// 키맵 id 를 실제 쓸 키맵으로 푼다: 프리셋이면 그대로, 아니면 사용자 키맵 + <c>basedOn</c> 프리셋. 모르면 <c>default</c>.
/// 셸의 <c>KeymapService.Reload</c> 가 프리셋 대신 이 함수를 부르면 사용자 정의가 적용된다.
/// </summary>
public static class KeymapResolver
{
    public static async Task<Keymap> ResolveAsync(string id, UserKeymapStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(store);
        if (KeymapLoader.PresetIds.Contains(id))
        {
            return KeymapLoader.LoadPreset(id);
        }

        var user = await store.LoadAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return KeymapLoader.LoadPreset("default");
        }

        var baseId = user.BasedOn is { } b && KeymapLoader.PresetIds.Contains(b) ? b : "default";
        return KeymapComposer.Compose(KeymapLoader.LoadPreset(baseId), user);
    }
}

/// <summary>
/// 설정 내보내기·가져오기 묶음(<c>oranda.tools.exportSettings</c>, <c>importSettings</c>): 설정 + 사용자 키맵 + 사용자 조각.
/// 접속 정보·비밀번호는 넣지 않는다. 다른 PC 에서 가져오면 같은 키맵·단축키가 된다.
/// </summary>
public sealed class SettingsTransfer(IAppDataPaths paths, UserKeymapStore keymaps)
{
    public const int BundleVersion = 1;

    public string SnippetsFile => Path.Combine(paths.DataDirectory, "snippets.json");

    public string Export(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var keymapArray = new JsonArray();
        foreach (var id in keymaps.ListIds())
        {
            try
            {
                using var stream = File.OpenRead(keymaps.PathOf(id));
                var km = KeymapLoader.Parse(stream);
                keymapArray.Add(JsonNode.Parse(KeymapLoader.Serialize(km)));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                // 읽을 수 없는 키맵 파일은 내보내지 않는다.
            }
        }

        var root = new JsonObject
        {
            ["bundleVersion"] = BundleVersion,
            ["settings"] = JsonSerializer.SerializeToNode(settings, OrandaJsonContext.Default.AppSettings),
            ["keymaps"] = keymapArray,
        };
        if (File.Exists(SnippetsFile))
        {
            root["snippets"] = JsonNode.Parse(File.ReadAllText(SnippetsFile));
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>묶음을 읽어 키맵·조각 파일을 쓰고 새 설정을 돌려준다(호출자가 설정 저장소에 저장). 형식이 틀리면 <see cref="InvalidDataException"/>.</summary>
    public async Task<AppSettings> ImportAsync(string json, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("설정 묶음 형식이 올바르지 않다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("설정 묶음 형식이 올바르지 않다: " + ex.Message, ex);
        }

        var version = root["bundleVersion"]?.GetValue<int>() ?? 0;
        if (version is < 1 or > BundleVersion || root["settings"] is not JsonObject settingsNode)
        {
            throw new InvalidDataException("지원하지 않는 설정 묶음이다.");
        }

        AppSettings settings;
        try
        {
            var merged = JsonSerializer.SerializeToNode(new AppSettings(), OrandaJsonContext.Default.AppSettings) as JsonObject ?? new JsonObject();
            Merge(merged, settingsNode);
            settings = merged.Deserialize(OrandaJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("설정 값을 읽을 수 없다: " + ex.Message, ex);
        }

        // 먼저 전부 읽어 검증한 뒤 쓴다(중간 실패로 반쪽 가져오기가 되지 않게).
        var parsedKeymaps = new List<Keymap>();
        if (root["keymaps"] is JsonArray array)
        {
            foreach (var node in array)
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(node!.ToJsonString()));
                parsedKeymaps.Add(KeymapLoader.Parse(stream));
            }
        }

        string? snippetsJson = null;
        if (root["snippets"] is { } snippetsNode)
        {
            snippetsJson = snippetsNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            _ = Oranda.Language.Snippets.UserSnippetFile.Parse(snippetsJson);
        }

        foreach (var km in parsedKeymaps)
        {
            if (!KeymapLoader.PresetIds.Contains(km.Id) && km.Id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                await keymaps.SaveAsync(km, ct).ConfigureAwait(false);
            }
        }

        if (snippetsJson is not null)
        {
            await JsonDocumentFile.WriteBytesAsync(SnippetsFile, System.Text.Encoding.UTF8.GetBytes(snippetsJson), ct).ConfigureAwait(false);
        }

        return settings;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject so && target[key] is JsonObject to)
            {
                Merge(to, so);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
