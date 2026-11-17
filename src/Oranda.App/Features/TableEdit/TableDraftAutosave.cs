using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「✎ 새 테이블」 탭 하나의 작업 저장(설계 18.7, TE-43). <see cref="Interval"/>(30초)마다와 탭을 닫을 때 편집 모델을 <see cref="ITableDraftStore"/> 에 덮어쓴다.
/// 마지막으로 쓴 것(또는 처음 받은 모델)과 JSON 이 같으면 쓰지 않는다 — 막 연 빈 탭은 아무것도 남기지 않는다.
/// 실행이 성공하거나 「버리기」 를 고르면 <see cref="DiscardAsync"/> 로 지운다. 끝난 뒤(<see cref="IsEnded"/>)에는 더 쓰지 않는다.
/// 닫기·버리기에서 저장소가 실패하면 예외를 그대로 내고 자동 저장은 계속 돈다(탭이 남으면 작업도 남는다). 만들기는 <see cref="TableDraftLibrary"/> 가 한다.
/// </summary>
public sealed class TableDraftAutosave : IAsyncDisposable
{
    /// <summary>자동 저장 간격(설계 18.7).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ITableDraftStore _store;
    private readonly string _instance;
    private readonly Func<TableDraft> _snapshot;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _lastJson;
    private ITimer? _timer;

    /// <param name="store">저장소.</param>
    /// <param name="instance">이 앱 인스턴스(<see cref="TableDraftInstance"/>). 저장분을 이 값으로 쥔다.</param>
    /// <param name="connectionName">접속 이름(저장분 목록·소유자 노드 메뉴의 기준).</param>
    /// <param name="dbms">접속 DBMS.</param>
    /// <param name="snapshot">지금 편집 모델. 타이머 스레드에서도 불린다 — 불변 모델의 참조만 돌려주면 된다.</param>
    /// <param name="baseline">처음 모델(새 탭 = 막 연 모델, 되살린 탭 = 저장분). 이것과 같으면 쓰지 않는다.</param>
    /// <param name="draftId">되살린 저장분의 id(이 인스턴스가 이미 쥐었다). 새 탭은 null(처음 쓸 때 정해진다).</param>
    /// <param name="time">시계(시험용).</param>
    internal TableDraftAutosave(
        ITableDraftStore store,
        string instance,
        string connectionName,
        Dbms dbms,
        Func<TableDraft> snapshot,
        TableDraft baseline,
        long? draftId,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _instance = instance;
        ConnectionName = connectionName;
        Dbms = dbms;
        _snapshot = snapshot;
        _time = time;
        _lastJson = TableDraftJson.Serialize(baseline);
        DraftId = draftId;
    }

    public string ConnectionName { get; }

    public Dbms Dbms { get; }

    /// <summary>저장분 id. 아직 한 번도 쓰지 않은 새 탭은 null. 다른 곳에서 지워졌거나 남이 쥐었으면 다음 저장 때 새 id 로 바뀐다.</summary>
    public long? DraftId { get; private set; }

    /// <summary>닫기·버리기·Dispose 뒤. 더 쓰지 않는다.</summary>
    public bool IsEnded { get; private set; }

    /// <summary>마지막으로 쓴 것(또는 처음 모델)과 지금 모델이 다른가.</summary>
    public bool HasUnsavedChanges => !IsEnded && TableDraftJson.Serialize(_snapshot()) != _lastJson;

    /// <summary>마지막 타이머 저장(시험이 기다린다). 타이머가 아직 안 돌았으면 끝난 작업.</summary>
    internal Task LastTick { get; private set; } = Task.CompletedTask;

    /// <summary>타이머를 켠다. 두 번 불러도 하나다.</summary>
    public void Start()
    {
        if (IsEnded || _timer is not null)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => LastTick = TickAsync(), null, Interval, Interval);
    }

    /// <summary>바뀌었으면 쓴다(이 인스턴스가 쥔 채). 썼으면 참. 끝났거나 저장소가 쓸 수 없으면 거짓.</summary>
    public async Task<bool> SaveIfChangedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return !IsEnded && await SaveCoreAsync(keepOpen: true, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 탭을 닫는다. <paramref name="keep"/> = 「남겨 두기」: 바뀌었으면 쓰고, 저장분을 놓는다(소유자 노드 「만들던 새 테이블…」로 다시 엶).
    /// 거짓 = 「버리기」(<see cref="DiscardAsync"/>).
    /// </summary>
    public async Task CloseAsync(bool keep, CancellationToken ct)
    {
        if (!keep)
        {
            await DiscardAsync(ct);
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (IsEnded)
            {
                return;
            }

            if (!await SaveCoreAsync(keepOpen: false, ct) && DraftId is { } id)
            {
                await _store.ReleaseAsync(id, _instance, ct);
            }

            End();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>「버리기」 또는 실행 성공: 이 인스턴스가 쥔 저장분을 지우고 끝낸다.</summary>
    public async Task DiscardAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsEnded)
            {
                return;
            }

            if (DraftId is { } id)
            {
                await _store.DeleteAsync(id, _instance, ct);
            }

            End();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 타이머만 끈다(쓰지 않는다). 앱을 끌 때는 먼저 <see cref="SaveIfChangedAsync"/> — 저장분은 이 인스턴스가 쥔 채 남고, 다음 실행에서
    /// 끝난 인스턴스의 것으로 보여 작업 공간 복원이 되살린다. 탭만 닫을 때는 <see cref="CloseAsync"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsEnded)
            {
                End();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TickAsync()
    {
        try
        {
            await SaveIfChangedAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 자동 저장 실패는 탭 일을 막지 않는다. 마지막 글을 바꾸지 않았으니 다음 차례에 다시 쓴다.
        }
    }

    /// <summary>게이트 안에서. 바뀌었으면 쓰고 참.</summary>
    private async Task<bool> SaveCoreAsync(bool keepOpen, CancellationToken ct)
    {
        var draft = _snapshot();
        var json = TableDraftJson.Serialize(draft);
        if (json == _lastJson)
        {
            return false;
        }

        var entry = await _store.SaveAsync(DraftId, ConnectionName, Dbms, draft, _instance, keepOpen, ct);
        if (entry is null)
        {
            return false; // 쓸 수 없는 파일
        }

        _lastJson = json;
        DraftId = entry.Id;
        return true;
    }

    /// <summary>게이트 안에서. 저장소 일이 성공한 뒤에만 부른다.</summary>
    private void End()
    {
        _timer?.Dispose();
        _timer = null;
        IsEnded = true;
    }
}
