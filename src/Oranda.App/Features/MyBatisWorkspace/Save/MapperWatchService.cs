using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.ViewModels;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>알림 띠의 종류(design.md 5.3).</summary>
public enum MapperBannerKind
{
    None,

    /// <summary>내 고침이 없어 디스크를 다시 읽었다(알림 한 줄).</summary>
    Reloaded,

    /// <summary>내가 안 고친 문장을 디스크 것으로 반영하고 내 고침은 남겼다.</summary>
    Merged,

    /// <summary>양쪽이 고친 문장이 있어 고르기를 기다린다. 이 동안 저장하지 않는다.</summary>
    Conflict,

    /// <summary>디스크에서 지워졌거나 옮겨졌다. 고침은 남긴다.</summary>
    Missing,

    /// <summary>디스크 파일을 매퍼로 읽지 못했다(쓰는 도중·깨진 xml). 다음 사건을 기다린다.</summary>
    Unreadable,
}

/// <summary>
/// 매퍼 작업 공간 하나의 감시 상태(알림 띠·상태 표시줄 구간이 보는 모델). UI 스레드에서 바뀐다.
/// </summary>
public sealed class MapperWatchState : ViewModelBase, IDisposable
{
    private MapperBannerKind _banner;
    private string _message = string.Empty;
    private IReadOnlyList<MapperMergeConflict> _conflicts = [];
    private bool _isWatching;

    internal MapperWatchState(SqlWorkspaceViewModel workspace) => Workspace = workspace;

    public SqlWorkspaceViewModel Workspace { get; }

    public IMyBatisWorkspaceSession? Session => Workspace.MyBatis;

    public MapperBannerKind Banner
    {
        get => _banner;
        internal set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
                OnPropertyChanged(nameof(HasConflicts));
            }
        }
    }

    /// <summary>띠를 보일 것이 있다.</summary>
    public bool HasBanner => Banner != MapperBannerKind.None;

    /// <summary>「차이 보고 고르기 / 파일 것으로 / 내 것으로」 단추를 보인다.</summary>
    public bool HasConflicts => Banner == MapperBannerKind.Conflict && Conflicts.Count > 0;

    public string Message
    {
        get => _message;
        internal set => SetProperty(ref _message, value);
    }

    /// <summary>양쪽이 고친 문장(<see cref="MapperBannerKind.Conflict"/> 일 때).</summary>
    public IReadOnlyList<MapperMergeConflict> Conflicts
    {
        get => _conflicts;
        internal set
        {
            if (SetProperty(ref _conflicts, value))
            {
                OnPropertyChanged(nameof(HasConflicts));
            }
        }
    }

    /// <summary>파일 감시가 돌고 있다(상태 표시줄 「파일 감시 중」).</summary>
    public bool IsWatching
    {
        get => _isWatching;
        internal set => SetProperty(ref _isWatching, value);
    }

    /// <summary>저장 처리기가 저장 중이다(감시 사건을 미룬다).</summary>
    internal bool IsSaving { get; set; }

    /// <summary>저장 중에 미룬 감시 사건이 있다.</summary>
    internal bool CheckPending { get; set; }

    /// <summary>디스크에서 지워졌거나 옮겨졌다(저장할 때 「같은 경로에 다시 쓰기 / 경로 골라 쓰기」).</summary>
    public bool IsDiskMissing { get; internal set; }

    /// <summary>방금 내가 쓴 원문의 해시(쓰기 ~ <c>Rebase</c> 사이에 온 감시 사건을 자기 쓰기로 무시한다).</summary>
    internal string? ExpectedHash { get; set; }

    internal string? WatchedPath { get; set; }

    internal IMapperFileWatcher? Watcher { get; set; }

    internal ITimer? Debounce { get; set; }

    internal object Gate { get; } = new();

    public void Dispose()
    {
        lock (Gate)
        {
            Debounce?.Dispose();
            Debounce = null;
        }

        Watcher?.Dispose();
        Watcher = null;
        IsWatching = false;
    }
}

/// <summary>
/// 매퍼 파일 감시와 밖에서 바뀜 3방향 병합(design.md 5.3). 호스트의 작업 공간마다 세션이 붙으면 감시를 켜고, 떼거나 닫으면 끈다.
/// 사건은 300 ms 디바운스 뒤 UI 스레드에서 디스크를 읽는다. 디스크 해시가 기준(또는 방금 쓴 원문) 해시와 같으면 무시한다(자기 쓰기).
/// 세션이 붙을 때도 한 번 견준다: 앱이 꺼진 동안 바뀐 파일(복구, 5.6)은 감시 사건이 오지 않는다.
/// 매개변수 없는 생성자만 둔다. Language 형은 <see cref="MapperDisk"/>·<see cref="MapperMerge"/> 안에서만 쓴다(D-006).
/// </summary>
[OrandaService(typeof(MapperWatchService))]
public sealed class MapperWatchService : IDisposable
{
    public const int DebounceMilliseconds = 300;

    private readonly Dictionary<SqlWorkspaceViewModel, MapperWatchState> _states = new(ReferenceEqualityComparer.Instance);
    private SqlEditorHost? _host;

    /// <summary>상태가 바뀌었다(띠·상태 표시줄 다시 그리기).</summary>
    public event EventHandler<MapperWatchState>? Changed;

    internal TimeProvider Time { get; set; } = TimeProvider.System;

    internal Func<string, IMapperFileWatcher> CreateWatcher { get; set; } = path => new MapperFileWatcher(path);

    /// <summary>디바운스 뒤 UI 스레드로 넘기는 자리(시험은 바로 부른다).</summary>
    internal Action<Action> Post { get; set; } = a => Dispatcher.UIThread.Post(a);

    /// <summary>다루는 호스트. <see cref="Attach"/> 전이면 null.</summary>
    public SqlEditorHost? Host => _host;

    /// <summary>호스트의 작업 공간 목록을 따른다. 두 번 부르면 앞 호스트를 놓는다.</summary>
    public void Attach(SqlEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (ReferenceEquals(_host, host))
        {
            return;
        }

        Detach();
        _host = host;
        host.Workspaces.CollectionChanged += OnWorkspacesChanged;
        foreach (var ws in host.Workspaces)
        {
            Track(ws);
        }
    }

    /// <summary>호스트를 따르지 않을 때(시험·호스트 붙기 전 저장)도 이 작업 공간의 상태를 둔다.</summary>
    internal MapperWatchState EnsureTracked(SqlWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (StateOf(workspace) is { } state)
        {
            return state;
        }

        Track(workspace);
        return _states[workspace];
    }

    public MapperWatchState? StateOf(SqlWorkspaceViewModel workspace) =>
        workspace is not null && _states.TryGetValue(workspace, out var s) ? s : null;

    public MapperWatchState? StateOf(IMyBatisWorkspaceSession session) =>
        _states.Values.FirstOrDefault(s => ReferenceEquals(s.Session, session));

    /// <summary>
    /// 지금 디스크를 읽어 견준다(UI 스레드). 내 고침이 없으면 다시 읽고, 충돌이 없으면 반영하고, 있으면 띠로 묻는다.
    /// 반영했거나 바뀐 것이 없으면 true, 충돌·파일 없음·못 읽음이면 false.
    /// </summary>
    public bool CheckNow(SqlWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.MyBatis is not { } session || StateOf(workspace) is not { } state)
        {
            return true;
        }

        var snap = MapperDisk.Read(session.MapperPath);
        if (!snap.Exists)
        {
            state.IsDiskMissing = true;
            Show(state, MapperBannerKind.Missing, Format("MyBatis.save.banner.missing",
                "{0} 이(가) 지워졌거나 옮겨졌습니다. 고침은 남아 있습니다. 저장하면 다시 쓸 경로를 묻습니다.", FileName(session)));
            return false;
        }

        state.IsDiskMissing = false;
        if (snap.Document is null)
        {
            Show(state, MapperBannerKind.Unreadable, Format("MyBatis.save.banner.unreadable", "{0} 을(를) 매퍼로 읽지 못했습니다: {1}", FileName(session), snap.Error));
            return false;
        }

        if (string.Equals(snap.Hash, session.BaselineHash, StringComparison.Ordinal)
            || string.Equals(snap.Hash, state.ExpectedHash, StringComparison.Ordinal))
        {
            // 디스크가 기준으로 돌아왔으면 충돌도 사라졌다.
            if (state.Banner is MapperBannerKind.Missing or MapperBannerKind.Unreadable or MapperBannerKind.Conflict)
            {
                state.Conflicts = [];
                Show(state, MapperBannerKind.None, string.Empty);
            }

            return true;
        }

        var plan = MapperMerge.Plan(session.Document, snap.Document, session.PendingEdits(), MapperDisk.Parser);
        if (plan.Conflicts.Count > 0)
        {
            state.Conflicts = plan.Conflicts;
            Show(state, MapperBannerKind.Conflict, ConflictMessage(session, plan));
            return false;
        }

        ApplyDisk(state, session, snap.Document.Text, plan.KeepMine, plan);
        return true;
    }

    /// <summary>
    /// 띠의 답(「파일 것으로」 = 모두 디스크, 「내 것으로」 = 모두 내 것, 「차이 보고 고르기」 = 문장마다). 디스크를 다시 읽고 계획을 다시 세운 뒤 반영한다
    /// (띠가 떠 있는 동안 고침·디스크가 또 바뀌었을 수 있다). 답이 없는 충돌은 내 것으로 둔다.
    /// </summary>
    public void Resolve(SqlWorkspaceViewModel workspace, IReadOnlyDictionary<string, MapperMergeChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(choices);
        if (workspace.MyBatis is not { } session || StateOf(workspace) is not { } state)
        {
            return;
        }

        var snap = MapperDisk.Read(session.MapperPath);
        if (snap.Document is null)
        {
            CheckNow(workspace); // 지워짐·못 읽음 띠로 바꾼다
            return;
        }

        if (string.Equals(snap.Hash, session.BaselineHash, StringComparison.Ordinal))
        {
            state.Conflicts = [];
            Show(state, MapperBannerKind.None, string.Empty);
            return;
        }

        var plan = MapperMerge.Plan(session.Document, snap.Document, session.PendingEdits(), MapperDisk.Parser);
        if (plan.Conflicts.Any(c => !choices.ContainsKey(c.Key)))
        {
            // 띠를 보인 뒤에 새로 생긴 충돌은 답 없이 내 것으로 두지 않는다: 새 목록으로 다시 묻는다.
            state.Conflicts = plan.Conflicts;
            Show(state, MapperBannerKind.Conflict, ConflictMessage(session, plan));
            return;
        }

        state.Conflicts = [];
        ApplyDisk(state, session, snap.Document.Text, plan.KeepMineWith(choices), plan);
    }

    /// <summary>모든 충돌에 같은 답.</summary>
    public void ResolveAll(SqlWorkspaceViewModel workspace, MapperMergeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var conflicts = StateOf(workspace)?.Conflicts ?? [];
        Resolve(workspace, conflicts.ToDictionary(c => c.Key, _ => choice, StringComparer.Ordinal));
    }

    /// <summary>띠를 닫는다(알림만인 띠). 충돌·파일 없음 띠는 닫지 않는다.</summary>
    public void Dismiss(SqlWorkspaceViewModel workspace)
    {
        if (StateOf(workspace) is { Banner: MapperBannerKind.Reloaded or MapperBannerKind.Merged or MapperBannerKind.Unreadable } state)
        {
            Show(state, MapperBannerKind.None, string.Empty);
        }
    }

    /// <summary>
    /// 저장 처리기: 저장 시작. 끝날 때까지 감시 사건은 세션을 바꾸지 않고 미룬다(「바뀌는 부분」 창이 떠 있는 동안 기준이 바뀌면
    /// 처리기가 옛 기준으로 만든 원문을 쓰게 된다). 미룬 사건은 <see cref="EndSave"/> 가 한 번 다시 본다.
    /// </summary>
    internal void BeginSave(SqlWorkspaceViewModel workspace) => EnsureTracked(workspace).IsSaving = true;

    internal void EndSave(SqlWorkspaceViewModel workspace)
    {
        if (StateOf(workspace) is not { } state)
        {
            return;
        }

        state.IsSaving = false;
        if (state.CheckPending)
        {
            state.CheckPending = false;
            Post(() => OnSettled(state));
        }
    }

    /// <summary>저장 처리기: 쓰기 직전. 그 원문 해시의 감시 사건은 자기 쓰기로 무시한다.</summary>
    internal void BeginWrite(SqlWorkspaceViewModel workspace, string expectedHash)
    {
        if (StateOf(workspace) is { } state)
        {
            state.ExpectedHash = expectedHash;
        }
    }

    /// <summary>저장 처리기: 쓰기 끝(성공이면 <c>Rebase</c> 뒤). 경로가 바뀌었으면 감시를 옮긴다.</summary>
    internal void EndWrite(SqlWorkspaceViewModel workspace, bool written)
    {
        if (StateOf(workspace) is not { } state)
        {
            return;
        }

        state.ExpectedHash = null;
        if (written)
        {
            state.IsDiskMissing = false;
            if (state.Banner is MapperBannerKind.Missing or MapperBannerKind.Reloaded or MapperBannerKind.Merged)
            {
                Show(state, MapperBannerKind.None, string.Empty);
            }

            Rewatch(state);
        }
    }

    public void Dispose() => Detach();

    private void Detach()
    {
        if (_host is { } old)
        {
            old.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        }

        foreach (var ws in _states.Keys.ToList())
        {
            Untrack(ws);
        }

        _host = null;
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _host is { } host)
        {
            foreach (var gone in _states.Keys.Where(w => !host.Workspaces.Contains(w)).ToList())
            {
                Untrack(gone);
            }

            foreach (var ws in host.Workspaces)
            {
                Track(ws);
            }

            return;
        }

        foreach (var ws in e.OldItems?.OfType<SqlWorkspaceViewModel>() ?? [])
        {
            Untrack(ws);
        }

        foreach (var ws in e.NewItems?.OfType<SqlWorkspaceViewModel>() ?? [])
        {
            Track(ws);
        }
    }

    private void Track(SqlWorkspaceViewModel ws)
    {
        if (_states.ContainsKey(ws))
        {
            return;
        }

        var state = new MapperWatchState(ws);
        _states[ws] = state;
        ws.PropertyChanged += OnWorkspacePropertyChanged;
        if (ws.MyBatis is not null)
        {
            StartSession(state);
        }
    }

    private void Untrack(SqlWorkspaceViewModel ws)
    {
        if (!_states.Remove(ws, out var state))
        {
            return;
        }

        ws.PropertyChanged -= OnWorkspacePropertyChanged;
        StopSession(state);
        state.Dispose();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SqlWorkspaceViewModel ws || StateOf(ws) is not { } state)
        {
            return;
        }

        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis))
        {
            StopSession(state);
            if (ws.MyBatis is not null)
            {
                StartSession(state);
            }
        }
        else if (e.PropertyName == nameof(SqlWorkspaceViewModel.IsDirty) && ws.MyBatis is { } session && !string.Equals(state.WatchedPath, session.MapperPath, StringComparison.Ordinal))
        {
            Rewatch(state); // Relocate(경로 골라 쓰기) 뒤
        }
    }

    private void StartSession(MapperWatchState state)
    {
        Rewatch(state);
        Raise(state);

        // 복구(03-01 이 곁 파일로 고침을 되살림)가 끝난 뒤 견주게 미룬다.
        Post(() => OnSettled(state));
    }

    private void StopSession(MapperWatchState state)
    {
        state.Dispose();
        state.WatchedPath = null;
        state.ExpectedHash = null;
        state.IsDiskMissing = false;
        state.Conflicts = [];
        state.Banner = MapperBannerKind.None;
        state.Message = string.Empty;
        Raise(state);
    }

    private void Rewatch(MapperWatchState state)
    {
        if (state.Session is not { } session || string.Equals(state.WatchedPath, session.MapperPath, StringComparison.Ordinal) && state.Watcher is { IsActive: true })
        {
            return;
        }

        state.Watcher?.Dispose();
        state.Watcher = null;
        state.WatchedPath = session.MapperPath;
        try
        {
            var watcher = CreateWatcher(session.MapperPath);
            watcher.Changed += (_, _) => OnRawEvent(state);
            state.Watcher = watcher;
            state.IsWatching = watcher.IsActive;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            state.IsWatching = false; // 감시를 못 켜도 저장 때 디스크 해시를 다시 보므로 고침은 안전하다
        }

        Raise(state);
    }

    // 스레드 풀: 마지막 사건에서 300 ms 조용하면 UI 스레드에서 견준다.
    private void OnRawEvent(MapperWatchState state)
    {
        lock (state.Gate)
        {
            var due = TimeSpan.FromMilliseconds(DebounceMilliseconds);
            if (state.Debounce is { } timer)
            {
                timer.Change(due, Timeout.InfiniteTimeSpan);
            }
            else
            {
                state.Debounce = Time.CreateTimer(_ => Post(() => OnSettled(state)), null, due, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnSettled(MapperWatchState state)
    {
        if (state.IsSaving)
        {
            state.CheckPending = true;
            return;
        }

        if (StateOf(state.Workspace) is { } now && ReferenceEquals(now, state) && state.Workspace.MyBatis is not null)
        {
            CheckNow(state.Workspace);
        }
    }

    private void ApplyDisk(MapperWatchState state, IMyBatisWorkspaceSession session, string diskText, IReadOnlyList<string> keepMine, MapperMergePlan plan)
    {
        session.ApplyDiskText(diskText, keepMine);
        state.Conflicts = [];
        if (!plan.HasMine)
        {
            Show(state, MapperBannerKind.Reloaded, Format("MyBatis.save.banner.reloaded", "{0} 이(가) 밖에서 바뀌어 다시 읽었습니다.", FileName(session)));
            return;
        }

        var message = Format("MyBatis.save.banner.merged", "{0} 이(가) 밖에서 바뀌었습니다. 내가 안 고친 문장 {1}개는 파일 것으로 반영하고 내 고침은 남겼습니다.",
            FileName(session), plan.DiskChanged.Count);
        if (plan.NewIdClashes.Count > 0)
        {
            message += " " + Format("MyBatis.save.banner.clash", "새 문장 id 가 파일과 겹칩니다: {0}. 저장 전에 id 를 바꾸십시오.", string.Join(", ", plan.NewIdClashes));
        }

        Show(state, MapperBannerKind.Merged, message);
    }

    private static string ConflictMessage(IMyBatisWorkspaceSession session, MapperMergePlan plan)
    {
        var ids = string.Join(", ", plan.Conflicts.Select(c => c.CurrentId));
        int others = plan.DiskChanged.Count(id => !plan.Conflicts.Any(c => string.Equals(c.Key, id, StringComparison.Ordinal)));
        return Format("MyBatis.save.banner.conflict", "{0} 이(가) 밖에서 바뀌었습니다. {1} 은(는) 양쪽 다 고쳤고, 나머지 바뀐 문장 {2}개는 내가 안 고친 문장입니다.",
            FileName(session), ids, others);
    }

    private void Show(MapperWatchState state, MapperBannerKind kind, string message)
    {
        state.Message = message;
        state.Banner = kind;
        Raise(state);
    }

    private void Raise(MapperWatchState state) => Changed?.Invoke(this, state);

    internal static string FileName(IMyBatisWorkspaceSession session) => Path.GetFileName(session.MapperPath);

    private static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);
}
