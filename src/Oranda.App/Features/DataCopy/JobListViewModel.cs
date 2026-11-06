using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>내 작업 목록의 한 줄.</summary>
public sealed class JobListItem(CopyJobEntry entry)
{
    public CopyJobEntry Entry { get; } = entry;

    public CopyJob Job => Entry.Job;

    public string Name => Job.Name;

    public bool IsReadOnly => Entry.IsReadOnly;

    public string? Notice => Entry.Notice;

    /// <summary>「2026-11-06 10:30 · 성공 · 1,234행」 같은 마지막 실행 요약. 실행한 적이 없으면 빈 글자.</summary>
    public string LastRunText => Job.LastRun is { } run
        ? string.Create(CultureInfo.InvariantCulture, $"{run.At.LocalDateTime:yyyy-MM-dd HH:mm} · {OutcomeText(run.Outcome)} · {run.Rows:N0}행")
        : string.Empty;

    private static string OutcomeText(CopyOutcome outcome) => outcome switch
    {
        CopyOutcome.Succeeded => "성공",
        CopyOutcome.PartiallyFailed => "일부 실패",
        CopyOutcome.Failed => "실패",
        _ => "중지",
    };
}

/// <summary>
/// 내 작업 메뉴(설계 6.5): 새로 만들기, 복제, 이름 바꾸기, 내보내기, 가져오기, 삭제. 이름·경로를 묻는 일은 창이 맡고 이 클래스는 값을 받아 저장소만 부른다.
/// 저장소(<see cref="ICopyJobStore"/>)가 없으면 예외 대신 <see cref="Notice"/> 에 알린다.
/// </summary>
public sealed partial class JobListViewModel(DataCopyServices services) : ViewModelBase
{
    [ObservableProperty]
    private JobListItem? _selected;

    [ObservableProperty]
    private string _notice = string.Empty;

    public ObservableCollection<JobListItem> Items { get; } = [];

    /// <summary>목록을 다시 읽는다. 읽은 뒤에도 같은 작업이 있으면 선택을 유지한다.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (services.Store is not { } store)
        {
            Notice = DataCopyText.StoreMissing;
            return;
        }

        var keep = Selected?.Job.Id;
        var entries = await store.ListAsync(ct).ConfigureAwait(true);
        Items.Clear();
        foreach (var entry in entries.OrderBy(e => e.Job.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new JobListItem(entry));
        }

        Selected = Items.FirstOrDefault(i => i.Job.Id == keep);
        Notice = string.Empty;
    }

    /// <summary>새 작업을 만들어 저장한다. 접속은 비워 두고(규칙 편집 단계에서 고른다) 해시 비밀 값(salt)은 이때 한 번 만든다.</summary>
    public async Task<CopyJob?> NewAsync(string name, CancellationToken ct = default)
    {
        if (services.Store is not { } store)
        {
            Notice = DataCopyText.StoreMissing;
            return null;
        }

        var job = new CopyJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Source = new ConnectionRef(string.Empty, string.Empty),
            Target = new ConnectionRef(string.Empty, string.Empty),
            SourceOwner = string.Empty,
            TargetOwner = string.Empty,
            MaskSalt = MaskFunctions.NewSalt(),
        };
        await store.SaveAsync(job, ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
        Selected = Items.FirstOrDefault(i => i.Job.Id == job.Id);
        return job;
    }

    public async Task<CopyJob?> DuplicateAsync(string newName, CancellationToken ct = default)
    {
        if (!TryStore(out var store) || Selected is not { } item)
        {
            return null;
        }

        var copy = await store.DuplicateAsync(item.Job.Id, newName, ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
        Selected = Items.FirstOrDefault(i => i.Job.Id == copy.Id);
        return copy;
    }

    public async Task RenameAsync(string newName, CancellationToken ct = default)
    {
        if (!TryStore(out var store) || Selected is not { IsReadOnly: false } item)
        {
            return;
        }

        await store.RenameAsync(item.Job.Id, newName, ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        if (TryStore(out var store) && Selected is { } item)
        {
            await store.ExportAsync(item.Job.Id, path, ct).ConfigureAwait(true);
        }
    }

    public async Task<CopyJob?> ImportAsync(string path, CancellationToken ct = default)
    {
        if (!TryStore(out var store))
        {
            return null;
        }

        var job = await store.ImportAsync(path, ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
        Selected = Items.FirstOrDefault(i => i.Job.Id == job.Id);
        return job;
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        if (!TryStore(out var store) || Selected is not { } item)
        {
            return;
        }

        await store.DeleteAsync(item.Job.Id, ct).ConfigureAwait(true);
        Selected = null;
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    private bool TryStore(out ICopyJobStore store)
    {
        if (services.Store is { } found)
        {
            store = found;
            return true;
        }

        Notice = DataCopyText.StoreMissing;
        store = null!;
        return false;
    }
}
