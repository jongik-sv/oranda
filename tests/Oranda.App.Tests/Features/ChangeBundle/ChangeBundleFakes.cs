using Oranda.App.Features.ChangeBundle;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.ChangeBundle;

// 소유: te-bundle (T9). 반영 묶음 창 시험용 가짜(메모리 저장소·서비스·대화상자·소유자 바꾸기).

/// <summary>메모리 묶음 저장소. 저장 규칙은 Platform 시험이 본다 — 여기서는 창이 부르는 순서·값만 본다.</summary>
internal sealed class MemoryBundleStore : IChangeBundleStore
{
    private readonly List<ChangeBundleInfo> _bundles = [];
    private readonly List<ChangeBundleItem> _items = [];
    private long _nextId = 1;

    public List<ChangeBundleRun> Runs { get; } = [];

    public bool IsWritable { get; set; } = true;

    public Task<IReadOnlyList<ChangeBundleInfo>> ListBundlesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ChangeBundleInfo>>([.. _bundles]);

    public Task<ChangeBundleInfo?> GetBundleAsync(long id, CancellationToken ct) => Task.FromResult(_bundles.Find(b => b.Id == id));

    public Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct) => Task.FromResult(_bundles.Find(b => b.IsCurrent));

    public Task<ChangeBundleInfo?> CreateAsync(string name, Dbms? dbms, IReadOnlyList<string> targetOwners, bool makeCurrent, CancellationToken ct)
    {
        if (makeCurrent)
        {
            for (var i = 0; i < _bundles.Count; i++)
            {
                _bundles[i] = _bundles[i] with { IsCurrent = false };
            }
        }

        var now = DateTimeOffset.UtcNow;
        var b = new ChangeBundleInfo(_nextId++, name, now, now, dbms, [.. targetOwners], makeCurrent);
        _bundles.Add(b);
        return Task.FromResult<ChangeBundleInfo?>(b);
    }

    public Task RenameAsync(long id, string name, CancellationToken ct) => Change(id, b => b with { Name = name });

    public Task UpdateTargetAsync(long id, Dbms? dbms, IReadOnlyList<string> targetOwners, CancellationToken ct) =>
        Change(id, b => b with { Dbms = dbms, TargetOwners = [.. targetOwners] });

    public Task SetCurrentAsync(long? id, CancellationToken ct)
    {
        for (var i = 0; i < _bundles.Count; i++)
        {
            _bundles[i] = _bundles[i] with { IsCurrent = _bundles[i].Id == id };
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken ct)
    {
        _bundles.RemoveAll(b => b.Id == id);
        _items.RemoveAll(i => i.BundleId == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeBundleItem>> ListItemsAsync(long bundleId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChangeBundleItem>>([.. _items.Where(i => i.BundleId == bundleId).OrderBy(i => i.Position)]);

    public Task<ChangeBundleItem?> FindSameAsync(long bundleId, string text, CancellationToken ct) =>
        Task.FromResult(_items.Find(i => i.BundleId == bundleId && i.Hash == StatementHash.Compute(text)));

    public Task<ChangeBundleItem?> AddItemAsync(long bundleId, NewChangeBundleItem item, CancellationToken ct)
    {
        var position = _items.Where(i => i.BundleId == bundleId).Select(i => i.Position).DefaultIfEmpty(0).Max() + 1;
        var added = new ChangeBundleItem(_nextId++, bundleId, position, item.Text, item.Source, item.TargetOwner, item.TargetName, true,
            DateTimeOffset.UtcNow, StatementHash.Compute(item.Text), item.Cautions, item.Warning);
        _items.Add(added);
        return Task.FromResult<ChangeBundleItem?>(added);
    }

    public Task UpdateItemTextAsync(long itemId, string text, CancellationToken ct)
    {
        Runs.RemoveAll(r => r.ItemId == itemId);
        return ChangeItem(itemId, i => i with { Text = text, Hash = StatementHash.Compute(text) });
    }

    public Task SetCheckedAsync(long itemId, bool isChecked, CancellationToken ct) => ChangeItem(itemId, i => i with { Checked = isChecked });

    public Task ReorderAsync(long bundleId, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        var position = 1;
        foreach (var id in itemIds)
        {
            var index = _items.FindIndex(i => i.Id == id && i.BundleId == bundleId);
            if (index >= 0)
            {
                _items[index] = _items[index] with { Position = position++ };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteItemAsync(long itemId, CancellationToken ct)
    {
        _items.RemoveAll(i => i.Id == itemId);
        Runs.RemoveAll(r => r.ItemId == itemId);
        return Task.CompletedTask;
    }

    public Task AddRunAsync(ChangeBundleRun run, CancellationToken ct)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<long, ChangeBundleRunMarks>> GetRunMarksAsync(long bundleId, CancellationToken ct)
    {
        var marks = new Dictionary<long, ChangeBundleRunMarks>();
        foreach (var g in Runs.Where(r => r.Succeeded).GroupBy(r => r.ItemId))
        {
            DateTimeOffset? Last(ConnectionEnvironment e) => g.Where(r => r.Environment == e).Select(r => (DateTimeOffset?)r.RanAt).Max();
            marks[g.Key] = new ChangeBundleRunMarks(Last(ConnectionEnvironment.Development), Last(ConnectionEnvironment.Test),
                Last(ConnectionEnvironment.Production));
        }

        return Task.FromResult<IReadOnlyDictionary<long, ChangeBundleRunMarks>>(marks);
    }

    /// <summary>시험 준비: 묶음 하나에 문장을 넣는다.</summary>
    public async Task<ChangeBundleInfo> SeedAsync(string name, Dbms dbms, IReadOnlyList<string> owners, params string[] texts)
    {
        var b = (await CreateAsync(name, dbms, owners, makeCurrent: true, default))!;
        foreach (var t in texts)
        {
            await AddItemAsync(b.Id, new NewChangeBundleItem(t, ChangeBundleItemSource.SqlEditor, owners.Count > 0 ? owners[0] : null, null), default);
        }

        return b;
    }

    private Task Change(long id, Func<ChangeBundleInfo, ChangeBundleInfo> f)
    {
        var index = _bundles.FindIndex(b => b.Id == id);
        if (index >= 0)
        {
            _bundles[index] = f(_bundles[index]);
        }

        return Task.CompletedTask;
    }

    private Task ChangeItem(long id, Func<ChangeBundleItem, ChangeBundleItem> f)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index >= 0)
        {
            _items[index] = f(_items[index]);
        }

        return Task.CompletedTask;
    }
}

/// <summary>저장소 위의 얇은 서비스(만들기·실행 기록만 — 자동 넣기는 Platform 시험).</summary>
internal sealed class MemoryBundleService(MemoryBundleStore store) : IChangeBundleService
{
    public ChangeBundleOrigin? Origin { get; set; }

    public Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct) => store.GetCurrentAsync(ct);

    public ChangeBundleOrigin? OriginOf(Guid sessionId) => Origin;

    public Task<ChangeBundleInfo?> CreateAsync(string name, ChangeBundleOrigin? origin, bool makeCurrent, CancellationToken ct) =>
        store.CreateAsync(name, null, origin is null ? [] : [origin.CurrentSchema], makeCurrent, ct);

    public Task<ChangeBundleAddResult> AddAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements, ChangeBundleItemSource source,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<ChangeBundleAddResult> AddExecutedAsync(ChangeBundleOrigin origin, SqlStatement statement, ChangeBundleItemSource source,
        CancellationToken ct) => throw new NotSupportedException();

    public Task RecordRunAsync(long itemId, ChangeBundleOrigin origin, bool succeeded, CancellationToken ct) =>
        store.AddRunAsync(new ChangeBundleRun(itemId, origin.Environment, origin.ConnectionName, DateTimeOffset.UtcNow, succeeded), ct);
}

/// <summary>답을 미리 정해 두는 대화상자. 실행·저장 창은 <see cref="OnRun"/>·<see cref="OnSave"/> 가 대신한다.</summary>
internal sealed class ScriptedBundleDialogs : IChangeBundleDialogs
{
    public Queue<bool> Confirms { get; } = [];

    public Queue<string?> Prompts { get; } = [];

    public Queue<ChangeBundleTextEdit?> Edits { get; } = [];

    public List<string> Asked { get; } = [];

    public Func<ChangeBundleRunViewModel, Task>? OnRun { get; set; }

    public Func<ChangeBundleSaveViewModel, Task>? OnSave { get; set; }

    public string? SavePath { get; set; }

    public Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct)
    {
        Asked.Add(title + ": " + message);
        return Task.FromResult(Confirms.Count > 0 && Confirms.Dequeue());
    }

    public Task<string?> PromptAsync(string title, string label, string initial, CancellationToken ct)
    {
        Asked.Add(title + ": " + initial);
        return Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);
    }

    public Task<ChangeBundleTextEdit?> EditTextAsync(string title, string text, CancellationToken ct)
    {
        Asked.Add(title + ": " + text);
        return Task.FromResult(Edits.Count > 0 ? Edits.Dequeue() : null);
    }

    public Task NoticeAsync(string title, string message, CancellationToken ct)
    {
        Asked.Add(title + ": " + message);
        return Task.CompletedTask;
    }

    public Task ShowRunAsync(ChangeBundleRunViewModel vm, CancellationToken ct) => OnRun?.Invoke(vm) ?? Task.CompletedTask;

    public Task ShowSaveAsync(ChangeBundleSaveViewModel vm, CancellationToken ct) => OnSave?.Invoke(vm) ?? Task.CompletedTask;

    public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken ct) => Task.FromResult(SavePath);
}
