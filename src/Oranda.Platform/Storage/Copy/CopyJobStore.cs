using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Storage;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Platform.Storage.Copy;

/// <summary>
/// 복사 작업 파일(<c>copy-jobs/{작업 이름}.json</c>) 저장·복제·이름 바꾸기·내보내기·가져오기.
/// 읽기는 <see cref="JsonDocumentFile"/>(손상 백업·미래 버전 읽기 전용), 쓰기는 원자적이다.
/// 작업은 파일 이름이 아니라 <see cref="CopyJob.Id"/> 로 찾는다. 파일에는 비밀번호를 담지 않는다(모델에 필드가 없다).
/// 가져오기는 기존 작업을 덮어쓰지 않는다: Id·이름이 겹치면 새 Id·이름을 붙여 저장한다.
/// </summary>
[OrandaService(typeof(ICopyJobStore))]
internal sealed partial class CopyJobStore : ICopyJobStore
{
    /// <summary>변환기 선택용 파일 종류. 아직 상향 변환이 없어 쓰이지 않는다.</summary>
    private const string FileKind = "copy-job";

    /// <summary>파일 이름 줄기의 UTF-8 바이트 상한. 접미사 ` (n)`·`.json`·`.tmp` 를 붙여도 255바이트 한도 안에 든다.</summary>
    private const int MaxFileStemBytes = 80;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly char[] ExtraInvalidFileNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly IAppDataPaths _paths;

    public CopyJobStore(IAppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<CopyJobEntry>> ListAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ListCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(CopyJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateName(job.Name);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            var existing = entries.FirstOrDefault(e => e.Job.Id == job.Id);
            if (existing is { IsReadOnly: true })
            {
                throw new InvalidOperationException(ReadOnlyMessage(existing));
            }

            var path = existing?.FilePath ?? UniquePath(job.Name, ownPath: null);
            await WriteAsync(path, job, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<CopyJob> DuplicateAsync(string id, string newName, CancellationToken ct)
    {
        ValidateName(newName);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            var source = Find(entries, id);
            if (source.IsReadOnly)
            {
                throw new InvalidOperationException(ReadOnlyMessage(source));
            }

            var copy = source.Job with
            {
                Id = NewId(entries),
                Name = newName.Trim(),
                LastRun = null,
            };
            await WriteAsync(UniquePath(copy.Name, ownPath: null), copy, ct).ConfigureAwait(false);
            return copy;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RenameAsync(string id, string newName, CancellationToken ct)
    {
        ValidateName(newName);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            var entry = Find(entries, id);
            if (entry.IsReadOnly)
            {
                throw new InvalidOperationException(ReadOnlyMessage(entry));
            }

            var renamed = entry.Job with { Name = newName.Trim() };
            var newPath = UniquePath(renamed.Name, ownPath: entry.FilePath);
            await WriteAsync(newPath, renamed, ct).ConfigureAwait(false);
            if (!SamePath(newPath, entry.FilePath))
            {
                DeleteFiles(entry.FilePath);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            DeleteFiles(Find(entries, id).FilePath);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ExportAsync(string id, string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            var entry = Find(entries, id);
            if (entry.IsReadOnly)
            {
                throw new InvalidOperationException(ReadOnlyMessage(entry));
            }

            await WriteAsync(path, entry.Job, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<CopyJob> ImportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var imported = ParseImport(bytes);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ListCoreAsync(ct).ConfigureAwait(false);
            var ids = entries.Select(e => e.Job.Id).ToHashSet(StringComparer.Ordinal);
            var names = entries.Select(e => e.Job.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var id = ids.Contains(imported.Id) || !IsSafeId(imported.Id) ? NewId(entries) : imported.Id;
            var name = imported.Name.Trim();
            var baseName = name;
            for (var n = 2; names.Contains(name); n++)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({n})");
            }

            // MaskSalt 는 그대로 둔다: 동료와 같은 마스킹 값으로 조인이 유지된다(설계 8.5).
            var saved = imported with { Id = id, Name = name, LastRun = null };
            await WriteAsync(UniquePath(name, ownPath: null), saved, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>작업 이름 → 파일 이름 줄기. 파일 이름에 쓸 수 없는 글자는 <c>_</c> 로 바꾼다.</summary>
    internal static string ToFileStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Trim().Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 || Array.IndexOf(ExtraInvalidFileNameChars, ch) >= 0 ? '_' : ch);
        }

        var cut = builder.ToString();
        if (Encoding.UTF8.GetByteCount(cut) > MaxFileStemBytes)
        {
            var kept = new StringBuilder();
            var bytes = 0;
            foreach (var rune in cut.EnumerateRunes())
            {
                bytes += rune.Utf8SequenceLength;
                if (bytes > MaxFileStemBytes)
                {
                    break;
                }

                kept.Append(rune.ToString());
            }

            cut = kept.ToString();
        }

        var stem = cut.TrimEnd(' ', '.');
        if (stem.Length == 0)
        {
            stem = "_";
        }

        // Windows 예약 이름(CON, NUL, COM1 …)은 확장자를 붙여도 열 수 없다.
        return ReservedName().IsMatch(stem) ? "_" + stem : stem;
    }

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("작업 이름이 비었다.", nameof(name));
        }
    }

    private static string ReadOnlyMessage(CopyJobEntry entry)
        => entry.Notice ?? $"새 버전 Oranda 가 만든 작업({entry.Job.Name})이라 읽기 전용이다.";

    private static CopyJobEntry Find(IReadOnlyList<CopyJobEntry> entries, string id)
        => entries.FirstOrDefault(e => e.Job.Id == id) ?? throw new FileNotFoundException($"복사 작업을 찾을 수 없다: {id}");

    private static string NewId(IReadOnlyList<CopyJobEntry> entries)
    {
        var ids = entries.Select(e => e.Job.Id).ToHashSet(StringComparer.Ordinal);
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (ids.Contains(id));

        return id;
    }

    /// <summary>실행 기록 파일 이름에 들어가는 Id 라서 짧은 영숫자·<c>-_</c> 만 받는다.</summary>
    private static bool IsSafeId(string id)
        => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void DeleteFiles(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    private static Task WriteAsync(string path, CopyJob job, CancellationToken ct)
        => JsonDocumentFile.WriteAsync(path, job with { SchemaVersion = CopyJobSchema.Current }, CopyJobJsonContext.Default.CopyJob, ct);

    /// <summary>가져올 파일을 읽는다. 사용자가 고른 파일이므로 손상돼도 백업 폴더로 옮기지 않고 거부한다.</summary>
    private static CopyJob ParseImport(byte[] bytes)
    {
        try
        {
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject obj
                || obj["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id)
                || obj["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("복사 작업 파일이 아니다(id·name 이 없다).");
            }

            var version = obj["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 0;
            if (version > CopyJobSchema.Current)
            {
                throw new InvalidDataException($"새 버전 Oranda 가 만든 복사 작업 파일(schemaVersion {version})이라 가져올 수 없다.");
            }

            var merged = JsonDocumentFile.OverDefaults(node, Placeholder(id), CopyJobJsonContext.Default.CopyJob);
            return merged.Deserialize(CopyJobJsonContext.Default.CopyJob) ?? throw new InvalidDataException("복사 작업을 읽을 수 없다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("복사 작업 파일 형식이 올바르지 않다: " + ex.Message, ex);
        }
    }

    private static CopyJob Placeholder(string idAndName) => new()
    {
        Id = idAndName,
        Name = idAndName,
        Source = new ConnectionRef(string.Empty, string.Empty),
        Target = new ConnectionRef(string.Empty, string.Empty),
        SourceOwner = string.Empty,
        TargetOwner = string.Empty,
        MaskSalt = string.Empty,
    };

    /// <summary>작업 이름에서 파일 경로를 정한다. 겹치면 <c> (2)</c>, <c> (3)</c> 을 붙인다.</summary>
    private string UniquePath(string name, string? ownPath)
    {
        var stem = ToFileStem(name);
        var path = Path.Combine(_paths.CopyJobsDirectory, stem + ".json");
        for (var n = 2; File.Exists(path) && (ownPath is null || !SamePath(path, ownPath)); n++)
        {
            path = Path.Combine(_paths.CopyJobsDirectory, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}).json"));
        }

        return path;
    }

    private async Task<IReadOnlyList<CopyJobEntry>> ListCoreAsync(CancellationToken ct)
    {
        var list = new List<CopyJobEntry>();
        if (!Directory.Exists(_paths.CopyJobsDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.CopyJobsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var result = await JsonDocumentFile.LoadAsync(
                file,
                FileKind,
                CopyJobSchema.Current,
                CopyJobJsonContext.Default.CopyJob,
                () => Placeholder(stem),
                [],
                _paths.BackupDirectory,
                ct).ConfigureAwait(false);

            // 없거나 손상(백업으로 옮김)·변환 실패는 목록에서 뺀다.
            if (result.State is JsonLoadState.Missing or JsonLoadState.Corrupt or JsonLoadState.MigrationFailed
                || string.IsNullOrWhiteSpace(result.Value.Id) || string.IsNullOrWhiteSpace(result.Value.Name))
            {
                continue;
            }

            list.Add(new CopyJobEntry(result.Value, file, result.IsReadOnly, result.Notice));
        }

        return list.OrderBy(e => e.Job.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
