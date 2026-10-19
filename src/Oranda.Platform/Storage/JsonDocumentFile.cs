using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Oranda.Core.Settings;

namespace Oranda.Platform.Storage;

/// <summary>JSON 파일을 읽은 결과(data-model 4.1).</summary>
/// <param name="Value">읽은 값. 파일이 없거나 쓸 수 없으면 기본값.</param>
/// <param name="State">읽기 결과 종류.</param>
/// <param name="IsReadOnly">true 면 저장하지 않는다(미래 버전·변환 실패).</param>
/// <param name="Notice">사용자에게 알릴 말. 없으면 null.</param>
public sealed record JsonLoadResult<T>(T Value, JsonLoadState State, bool IsReadOnly, string? Notice);

public enum JsonLoadState
{
    /// <summary>파일이 없어 기본값.</summary>
    Missing,

    /// <summary>현재 버전 그대로 읽음.</summary>
    Current,

    /// <summary>옛 버전을 변환해 읽고 다시 썼다.</summary>
    Migrated,

    /// <summary>손상: 원본을 backup 으로 옮기고 기본값.</summary>
    Corrupt,

    /// <summary>미래 버전: 읽기 전용.</summary>
    FutureVersion,

    /// <summary>변환 실패: 원본 그대로, 읽기 전용, 기본값.</summary>
    MigrationFailed,
}

/// <summary>
/// JSON 파일 공통 처리(원자적 쓰기·schemaVersion·마이그레이션·손상 백업, data-model 1.3·4.1). 다른 저장소가 쓴다.
/// 소유: TSK-03-01.
/// </summary>
public static class JsonDocumentFile
{
    /// <summary>종류별 최근 백업 보관 개수(data-model 4.1).</summary>
    public const int BackupsToKeep = 3;

    /// <summary>
    /// 파일을 읽는다. 없으면 기본값, 손상이면 <c>backup/{이름}.corrupt-{UTC}</c> 로 옮기고 기본값,
    /// 옛 버전이면 백업 후 단계별 변환·다시 쓰기, 미래 버전이면 아는 필드만 읽고 읽기 전용.
    /// </summary>
    /// <param name="path">파일 경로.</param>
    /// <param name="fileKind"><see cref="SettingsFileKinds"/> 값(변환기 선택).</param>
    /// <param name="currentVersion">앱이 아는 현재 <c>schemaVersion</c>.</param>
    /// <param name="typeInfo">소스 생성 직렬화 정보.</param>
    /// <param name="createDefault">기본값 만들기.</param>
    /// <param name="migrations">변환기 전체(종류·버전으로 골라 쓴다).</param>
    /// <param name="backupDirectory">백업 폴더.</param>
    /// <param name="now">시각 공급(시험). 기본 UTC 현재.</param>
    public static async Task<JsonLoadResult<T>> LoadAsync<T>(
        string path,
        string fileKind,
        int currentVersion,
        JsonTypeInfo<T> typeInfo,
        Func<T> createDefault,
        IEnumerable<ISettingsMigration> migrations,
        string backupDirectory,
        CancellationToken ct,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(createDefault);
        var clock = now ?? (() => DateTimeOffset.UtcNow);
        var name = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            return new JsonLoadResult<T>(createDefault(), JsonLoadState.Missing, false, null);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new JsonLoadResult<T>(createDefault(), JsonLoadState.Missing, false, null);
        }

        JsonNode? node = null;
        var version = 0;
        try
        {
            node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            version = ReadVersion(node);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is null || version < 1)
        {
            return Corrupt(path, name, backupDirectory, clock, createDefault, "형식이 올바르지 않다");
        }

        if (version > currentVersion)
        {
            var future = TryDeserialize(OverDefaults(node, createDefault(), typeInfo), typeInfo);
            return new JsonLoadResult<T>(
                future is not null ? future : createDefault(),
                JsonLoadState.FutureVersion,
                true,
                $"{name} 은(는) 새 버전 Oranda 가 만든 파일(schemaVersion {version})이라 읽기 전용으로 연다.");
        }

        if (version == currentVersion)
        {
            var value = TryDeserialize(OverDefaults(node, createDefault(), typeInfo), typeInfo);
            return value is not null
                ? new JsonLoadResult<T>(value, JsonLoadState.Current, false, null)
                : Corrupt(path, name, backupDirectory, clock, createDefault, "값을 읽을 수 없다");
        }

        return await MigrateAsync(path, name, fileKind, version, currentVersion, node, typeInfo, createDefault, migrations, backupDirectory, clock, ct)
            .ConfigureAwait(false);
    }

    /// <summary>값을 임시 파일에 쓰고 flush 한 뒤 원본을 바꾼다(원자적). 실패하면 원본은 그대로.</summary>
    public static async Task WriteAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await WriteBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>바이트를 원자적으로 쓴다.</summary>
    public static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static async Task<JsonLoadResult<T>> MigrateAsync<T>(
        string path,
        string name,
        string fileKind,
        int version,
        int currentVersion,
        JsonNode node,
        JsonTypeInfo<T> typeInfo,
        Func<T> createDefault,
        IEnumerable<ISettingsMigration> migrations,
        string backupDirectory,
        Func<DateTimeOffset> clock,
        CancellationToken ct)
    {
        var steps = migrations.Where(m => m.FileKind == fileKind).ToDictionary(m => m.FromVersion);

        Backup(path, name, version, backupDirectory, clock());
        try
        {
            var work = node.DeepClone();
            for (var v = version; v < currentVersion; v++)
            {
                if (!steps.TryGetValue(v, out var step))
                {
                    throw new InvalidOperationException($"{fileKind} 의 {v}→{v + 1} 변환기가 없다.");
                }

                work = step.Apply(work);
                work["schemaVersion"] = v + 1;
            }

            var value = TryDeserialize(OverDefaults(work, createDefault(), typeInfo), typeInfo) ?? throw new InvalidOperationException("변환한 값을 읽을 수 없다.");
            await WriteAsync(path, value, typeInfo, ct).ConfigureAwait(false);
            return new JsonLoadResult<T>(value, JsonLoadState.Migrated, false, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return new JsonLoadResult<T>(
                createDefault(),
                JsonLoadState.MigrationFailed,
                true,
                $"{name} (schemaVersion {version}) 을(를) 변환하지 못해 읽기 전용으로 연다: {ex.Message}");
        }
    }

    private static JsonLoadResult<T> Corrupt<T>(
        string path, string name, string backupDirectory, Func<DateTimeOffset> clock, Func<T> createDefault, string reason)
    {
        string? movedTo = null;
        try
        {
            Directory.CreateDirectory(backupDirectory);
            movedTo = Path.Combine(backupDirectory, $"{name}.corrupt-{Stamp(clock())}");
            File.Move(path, movedTo, overwrite: true);
        }
        catch (IOException)
        {
            movedTo = null;
        }
        catch (UnauthorizedAccessException)
        {
            movedTo = null;
        }

        var notice = movedTo is null
            ? $"{name} 이(가) 손상되어({reason}) 기본값으로 시작한다."
            : $"{name} 이(가) 손상되어({reason}) {movedTo} 로 옮기고 기본값으로 시작한다.";
        return new JsonLoadResult<T>(createDefault(), JsonLoadState.Corrupt, false, notice);
    }

    private static void Backup(string path, string name, int oldVersion, string backupDirectory, DateTimeOffset at)
    {
        Directory.CreateDirectory(backupDirectory);
        File.Copy(path, Path.Combine(backupDirectory, $"{name}.v{oldVersion}.{Stamp(at)}.bak"), overwrite: true);

        var old = new DirectoryInfo(backupDirectory)
            .GetFiles($"{name}.v*.bak")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(BackupsToKeep);
        foreach (var f in old)
        {
            TryDelete(f.FullName);
        }
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 파일 내용을 기본값 JSON 위에 겹쳐 「빠진 필드 = 기본값」(data-model 1.3)을 보장한다.
    /// 소스 생성 역직렬화가 속성 초기값을 쓰지 않는 경우가 있어(빠진 섹션이 null) 노드 단계에서 채운다.
    /// </summary>
    internal static JsonNode OverDefaults<T>(JsonNode file, T defaults, JsonTypeInfo<T> typeInfo)
    {
        if (file is not JsonObject fileObject || JsonSerializer.SerializeToNode(defaults, typeInfo) is not JsonObject baseObject)
        {
            return file;
        }

        Merge(baseObject, fileObject);
        return baseObject;
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

    private static int ReadVersion(JsonNode? node)
    {
        if (node is JsonObject obj
            && obj["schemaVersion"] is JsonValue v
            && v.TryGetValue<int>(out var version))
        {
            return version;
        }

        return 0;
    }

    private static T? TryDeserialize<T>(JsonNode node, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return node.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 정리 실패는 무시한다.
        }
        catch (UnauthorizedAccessException)
        {
            // 정리 실패는 무시한다.
        }
    }
}
