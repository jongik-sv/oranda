using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 새 테이블 작업 저장분의 앱 쪽 창구(설계 18.7, TE-43). 「✎ 새 테이블」 탭은 <see cref="Begin"/>·<see cref="ResumeAsync"/> 로 자동 저장을 받고,
/// 작업 공간 복원은 <see cref="ListForRestoreAsync"/>(끝난 인스턴스가 열어 둔 채 남긴 것), 소유자 노드 「만들던 새 테이블…」은 <see cref="ListForOwnerAsync"/> 로 목록을 얻는다.
/// 살아 있는 인스턴스(이 앱 포함)가 쥔 저장분은 두 목록에서 빠지고, 다시 열기는 저장소의 쥐기(<see cref="ITableDraftStore.ClaimAsync"/>)로 하나만 이긴다 —
/// 한 저장분이 두 탭(두 앱)에 열리지 않는다. 화면 연결은 te-ui2 몫.
/// </summary>
[OrandaService(typeof(TableDraftLibrary))]
public sealed class TableDraftLibrary(ITableDraftStore store)
{
    /// <summary>이 앱 인스턴스(시험용으로 바꿀 수 있다).</summary>
    public string Instance { get; init; } = TableDraftInstance.Current;

    /// <summary>인스턴스가 살아 있나(시험용으로 바꿀 수 있다). 이 앱 인스턴스는 늘 산 것으로 본다.</summary>
    public Func<string, bool> IsInstanceAlive { get; init; } = TableDraftInstance.IsAlive;

    /// <summary>시계(시험용).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>앱을 다시 켰을 때 탭으로 되살릴 저장분(끝난 인스턴스가 열린 채 남긴 것, 최근 순).</summary>
    public async Task<IReadOnlyList<TableDraftEntry>> ListForRestoreAsync(CancellationToken ct)
        => (await store.ListAsync(null, null, ct)).Where(e => e.OpenBy is not null && !IsHeldByLiveInstance(e)).ToList();

    /// <summary>소유자 노드 「만들던 새 테이블…」 목록(남겨 둔 것과 끝난 인스턴스가 남긴 것, 최근 순). 살아 있는 인스턴스가 쥔 것은 뺀다.</summary>
    public async Task<IReadOnlyList<TableDraftEntry>> ListForOwnerAsync(string connectionName, string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return (await store.ListAsync(connectionName, owner, ct)).Where(e => !IsHeldByLiveInstance(e)).ToList();
    }

    /// <summary>저장분 하나(모델 포함). 없거나 모델이 깨졌으면 null.</summary>
    public Task<StoredTableDraft?> GetAsync(long id, CancellationToken ct) => store.GetAsync(id, ct);

    /// <summary>
    /// 목록에서 지우기. 목록을 읽은 뒤 누가 쥐었으면(<paramref name="entry"/> 의 쥔 값과 달라졌으면) 지우지 않고 거짓.
    /// 살아 있는 인스턴스가 쥔 것도 거짓 — 탭의 「버리기」 로 지운다.
    /// </summary>
    public async Task<bool> DeleteAsync(TableDraftEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !IsHeldByLiveInstance(entry) && await store.DeleteAsync(entry.Id, entry.OpenBy, ct);
    }

    /// <summary>새 테이블 탭의 자동 저장을 시작한다. <paramref name="initial"/> 과 같으면(아무것도 안 고쳤으면) 쓰지 않는다.</summary>
    public TableDraftAutosave Begin(string connectionName, Dbms dbms, TableDraft initial, Func<TableDraft> snapshot)
    {
        var autosave = new TableDraftAutosave(store, Instance, connectionName, dbms, snapshot, initial, null, Time);
        autosave.Start();
        return autosave;
    }

    /// <summary>
    /// 저장분을 탭으로 다시 열 때(작업 공간 복원·소유자 노드 메뉴). 이 인스턴스가 쥐고 자동 저장을 시작한다.
    /// 그 사이 다른 탭·다른 앱이 쥐었거나 지워졌으면 null(목록을 다시 읽는다).
    /// </summary>
    public async Task<TableDraftAutosave?> ResumeAsync(StoredTableDraft stored, Func<TableDraft> snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var entry = stored.Entry;
        if (IsHeldByLiveInstance(entry) || !await store.ClaimAsync(entry.Id, entry.OpenBy, Instance, ct))
        {
            return null;
        }

        var autosave = new TableDraftAutosave(store, Instance, entry.ConnectionName, entry.Dbms, snapshot, stored.Draft, entry.Id, Time);
        autosave.Start();
        return autosave;
    }

    private bool IsHeldByLiveInstance(TableDraftEntry entry)
        => entry.OpenBy is { } holder && (holder == Instance || IsInstanceAlive(holder));
}
