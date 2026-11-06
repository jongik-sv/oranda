namespace Oranda.Core.Transfer.Copy;

/// <summary>복사 작업 파일 저장소. 구현: Oranda.Platform(<c>copy-jobs/</c>).</summary>
public interface ICopyJobStore
{
    Task<IReadOnlyList<CopyJobEntry>> ListAsync(CancellationToken ct);
    Task SaveAsync(CopyJob job, CancellationToken ct);
    Task<CopyJob> DuplicateAsync(string id, string newName, CancellationToken ct);
    Task RenameAsync(string id, string newName, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task ExportAsync(string id, string path, CancellationToken ct);
    Task<CopyJob> ImportAsync(string path, CancellationToken ct);
}

public sealed record CopyJobEntry(CopyJob Job, string FilePath, bool IsReadOnly, string? Notice);
