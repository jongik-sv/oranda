using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 파일 저장소(결정 71): <c>{data}/plsql-workspaces/{id}.json</c> 과 열린 목록 <c>open.json</c>.
/// 쓰기는 <c>.tmp</c> 에 쓰고 원본을 바꾼다(원자적, 일반 작업 공간 저장소와 같은 방식). 손상 파일은 <c>backup/</c> 으로 옮기고 없는 것처럼 다룬다.
/// 새 버전 앱이 쓴 파일(판이 더 큼)은 읽기만 하고 덮어쓰지 않는다. 문서를 닫아 치운 작업 공간도 지우지 않고 <c>backup/</c> 으로 옮긴다(최근 <see cref="ClosedKept"/> 개).
/// </summary>
public sealed partial class PlsqlWorkspaceStore
{
    /// <summary>backup/ 에 남겨 두는 닫힌 작업 공간 파일 수.</summary>
    public const int ClosedKept = 20;

    private const string IndexName = "open";

    private const string ClosedMark = ".closed-";

    // 자동 저장 타이머와 종료 저장이 .tmp 를 두고 다투지 않게 쓰기를 하나씩 한다(일반 작업 공간 저장소와 같다). 시험은 이것을 잡아 쓰기를 붙잡아 둔다.
    internal static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly HashSet<string> _readOnlyIds = new(StringComparer.Ordinal);
    private volatile bool _indexReadOnly;

    public PlsqlWorkspaceStore(string directory, string backupDirectory)
    {
        Folder = directory;
        BackupDirectory = backupDirectory;
    }

    /// <summary>작업 공간 파일 폴더(<c>{data}/plsql-workspaces</c>).</summary>
    public string Folder { get; }

    public string BackupDirectory { get; }

    /// <summary>열린 목록을 새 버전 앱이 썼다(읽기만 하고 덮어쓰지 않는다).</summary>
    public bool IsIndexReadOnly => _indexReadOnly;

    // 끝은 \z: $ 는 끝의 줄바꿈 앞에서도 맞는다.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z")]
    private static partial Regex IdPattern();

    /// <summary>작업 공간 ID 로 쓸 수 있는가(파일 이름이 된다).</summary>
    public static bool IsValidId(string? id) => id is not null && id != IndexName && IdPattern().IsMatch(id);

    /// <summary>작업 공간 하나를 읽는다. 없거나 손상(백업으로 옮김)이면 null. 파일 안 ID 가 파일 이름과 다르거나 탭 목록이 비정상이어도 손상이다.</summary>
    public async Task<PlsqlWorkspaceFile?> LoadAsync(string id, CancellationToken ct = default)
    {
        var (file, newer) = await ReadAsync(
            PathOf(id),
            PlsqlWorkspaceJsonContext.Default.PlsqlWorkspaceFile,
            f => string.Equals(f.Id, id, StringComparison.Ordinal) && f.Name is not null && f.Tabs is not null && f.Tabs.All(t => t is { Text: not null }),
            ct).ConfigureAwait(false);
        if (newer)
        {
            lock (_readOnlyIds)
            {
                _readOnlyIds.Add(id);
            }
        }

        return file;
    }

    /// <summary>새 버전 앱이 쓴 작업 공간 파일이다(읽은 적 있을 때만 안다). 복원하지 않고 덮어쓰지 않는다.</summary>
    public bool IsReadOnly(string id)
    {
        lock (_readOnlyIds)
        {
            return _readOnlyIds.Contains(id);
        }
    }

    /// <summary>작업 공간 하나를 쓴다. 새 버전 앱이 쓴 파일은 덮어쓰지 않는다.</summary>
    public Task SaveAsync(PlsqlWorkspaceFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_readOnlyIds)
        {
            if (_readOnlyIds.Contains(file.Id))
            {
                return Task.CompletedTask;
            }
        }

        return WriteAsync(PathOf(file.Id), Serialize(file), ct);
    }

    /// <summary>열린 목록을 읽는다. 없거나 손상이면 null. 새 버전 앱이 쓴 목록이면 이 뒤로 <see cref="SaveIndexAsync"/> 가 쓰지 않는다.</summary>
    public async Task<PlsqlWorkspaceIndex?> LoadIndexAsync(CancellationToken ct = default)
    {
        var (index, newer) = await ReadAsync(
            Path.Combine(Folder, IndexName + ".json"),
            PlsqlWorkspaceJsonContext.Default.PlsqlWorkspaceIndex,
            i => i.OpenIds is not null && i.OpenIds.All(id => id is not null),
            ct).ConfigureAwait(false);
        _indexReadOnly |= newer;
        return index;
    }

    public Task SaveIndexAsync(PlsqlWorkspaceIndex index, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        return _indexReadOnly ? Task.CompletedTask : WriteAsync(Path.Combine(Folder, IndexName + ".json"), Serialize(index), ct);
    }

    /// <summary>
    /// 문서를 닫아 치운 작업 공간 파일을 <c>backup/{id}.json.closed-{시각}</c> 으로 옮긴다(손으로 되살릴 수 있게). 닫힌 파일은 최근 <see cref="ClosedKept"/> 개만 남긴다.
    /// </summary>
    public async Task RetireAsync(string id, CancellationToken ct = default)
    {
        var path = PathOf(id);
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            MoveToBackup(path, ClosedMark);
            // 이름 끝의 시각(yyyyMMddHHmmssfff)으로 새것부터 세어 오래된 것을 지운다.
            var closed = Directory.EnumerateFiles(BackupDirectory, "*.json" + ClosedMark + "*")
                .OrderByDescending(f => f[(f.LastIndexOf(ClosedMark, StringComparison.Ordinal) + ClosedMark.Length)..], StringComparer.Ordinal)
                .Skip(ClosedKept);
            foreach (var old in closed.ToList())
            {
                TryDelete(old);
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>작업 공간 하나의 JSON(자동 저장이 바뀌었는지 견주는 표지로도 쓴다).</summary>
    public static string Serialize(PlsqlWorkspaceFile file) => JsonSerializer.Serialize(file, PlsqlWorkspaceJsonContext.Default.PlsqlWorkspaceFile);

    /// <summary>열린 목록의 JSON(자동 저장이 바뀌었는지 견주는 표지로도 쓴다).</summary>
    public static string Serialize(PlsqlWorkspaceIndex index) => JsonSerializer.Serialize(index, PlsqlWorkspaceJsonContext.Default.PlsqlWorkspaceIndex);

    private string PathOf(string id) =>
        IsValidId(id)
            ? Path.Combine(Folder, id + ".json")
            : throw new ArgumentException($"PL/SQL 작업 공간 ID 형식이 아니다: '{id}' (^[a-z0-9][a-z0-9-]{{0,63}}$)", nameof(id));

    private async Task WriteAsync(string path, string json, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var tmp = path + ".tmp";
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(json), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// 파일을 읽는다. 판(<c>schemaVersion</c>)을 먼저 본다: 더 큰 판(새 버전 앱)이면 읽을 수 있는 만큼 읽고 옮기지 않는다(<c>Newer</c>).
    /// 판이 없거나 1 보다 작고, 파싱 불가·필수 필드 없음·<paramref name="valid"/> 거짓이면 손상이다(backup/ 으로 옮기고 null).
    /// </summary>
    private async Task<(T? Value, bool Newer)> ReadAsync<T>(string path, JsonTypeInfo<T> type, Func<T, bool> valid, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return (null, false);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return (null, false);
        }

        var version = VersionOf(text);
        if (version > PlsqlWorkspaceFile.CurrentVersion)
        {
            return (TryDeserialize(text, type, valid), true);
        }

        if (version >= 1 && TryDeserialize(text, type, valid) is { } value)
        {
            return (value, false);
        }

        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MoveToBackup(path, ".corrupt-");
        }
        finally
        {
            WriteLock.Release();
        }

        return (null, false);
    }

    private static int VersionOf(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject root && root["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var version) ? version : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static T? TryDeserialize<T>(string text, JsonTypeInfo<T> type, Func<T, bool> valid)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(text, type) is { } value && valid(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void MoveToBackup(string path, string mark)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            File.Move(path, Path.Combine(BackupDirectory, $"{Path.GetFileName(path)}{mark}{stamp}"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 백업 실패해도 앱은 계속 동작한다.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
