using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Workspaces;

namespace Oranda.Platform.Storage;

/// <summary>
/// 작업 공간(<c>workspaces/{id}.json</c>)과 복구용 자동 저장(<c>autosave/{id}.json</c>) — data-model 2.6.
/// 쓰기는 <c>.tmp</c> 에 쓰고 원본을 바꾼다(원자적). 손상 파일은 <c>backup/</c> 으로 옮기고 없는 것처럼 다룬다.
/// 미래 버전 파일은 읽기 전용(저장하지 않음). 소유: TSK-04-02.
/// </summary>
[OrandaService(typeof(IWorkspaceStore))]
internal sealed partial class WorkspaceStore(IAppDataPaths paths) : IWorkspaceStore
{
    // 파일 하나씩 직렬화해 자동 저장 타이머와 수동 저장이 .tmp 를 두고 다투지 않게 한다.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly HashSet<string> _readOnlyIds = new(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    public async Task<StoredDocument<Workspace>?> LoadAsync(string id, CancellationToken ct)
    {
        var path = PathOf(paths.WorkspacesDirectory, id);
        var read = await ReadAsync(path, ct).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        if (read.Value.Workspace is null)
        {
            return null; // 손상 → 이미 백업됨
        }

        var ws = read.Value.Workspace;
        if (read.Value.IsFuture)
        {
            lock (_readOnlyIds)
            {
                _readOnlyIds.Add(id);
            }

            return new StoredDocument<Workspace>(ws, true) { Notice = "새 버전 Oranda 가 만든 작업 공간 파일이라 읽기만 한다." };
        }

        return new StoredDocument<Workspace>(ws) { Notice = read.Value.Notice };
    }

    public async Task SaveAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_readOnlyIds)
        {
            if (_readOnlyIds.Contains(workspace.Id))
            {
                return; // 미래 버전 파일은 덮어쓰지 않는다.
            }
        }

        await WriteAsync(PathOf(paths.WorkspacesDirectory, workspace.Id), workspace, ct).ConfigureAwait(false);
    }

    public Task AutosaveAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return WriteAsync(PathOf(paths.AutosaveDirectory, workspace.Id), workspace, ct);
    }

    public async Task<Workspace?> LoadAutosaveAsync(string id, CancellationToken ct)
    {
        var read = await ReadAsync(PathOf(paths.AutosaveDirectory, id), ct).ConfigureAwait(false);
        return read?.Workspace;
    }

    public async Task DeleteAutosaveAsync(string id, CancellationToken ct)
    {
        var path = PathOf(paths.AutosaveDirectory, id);
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            File.Delete(path); // 없어도 예외 없음
            File.Delete(path + ".tmp");
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static string PathOf(string directory, string id)
    {
        if (id is null || !IdPattern().IsMatch(id))
        {
            throw new ArgumentException($"작업 공간 ID 형식이 아니다: '{id}' (^[a-z0-9][a-z0-9-]{{0,63}}$)", nameof(id));
        }

        return Path.Combine(directory, id + ".json");
    }

    private static async Task WriteAsync(string path, Workspace workspace, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(workspace with { SchemaVersion = SchemaVersions.Workspace }, OrandaJsonContext.Default.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new System.Text.UTF8Encoding(false).GetBytes(json), ct).ConfigureAwait(false);
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

    private readonly record struct ReadResult(Workspace? Workspace, bool IsFuture, string? Notice);

    private async Task<ReadResult?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj
                || obj["schemaVersion"] is not JsonValue sv
                || !sv.TryGetValue<int>(out var version)
                || version < 1)
            {
                throw new JsonException("schemaVersion 없음");
            }

            var ws = JsonSerializer.Deserialize(text, OrandaJsonContext.Default.Workspace)
                     ?? throw new JsonException("빈 문서");
            return new ReadResult(ws, version > SchemaVersions.Workspace, null);
        }
        catch (JsonException)
        {
            // 파싱 불가·필수 필드 없음 = 손상. backup/ 으로 옮기고 없는 것처럼 시작한다.
            MoveToBackup(path);
            return new ReadResult(null, false, "손상된 작업 공간 파일을 backup 폴더로 옮겼다.");
        }
    }

    private void MoveToBackup(string path)
    {
        try
        {
            Directory.CreateDirectory(paths.BackupDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, Path.Combine(paths.BackupDirectory, $"{Path.GetFileName(path)}.corrupt-{stamp}"), overwrite: true);
        }
        catch (IOException)
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
        catch (IOException)
        {
        }
    }
}
