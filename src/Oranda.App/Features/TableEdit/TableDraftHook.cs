using System.Collections.Concurrent;
using Oranda.Core.Composition;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「✎ 새 테이블」 탭과 작업 저장(<see cref="TableDraftLibrary"/>·<see cref="TableDraftAutosave"/>)을 잇는다(설계 18.7, TE-43, T16d).
/// 새 탭은 <see cref="Attach"/>(접속 이름·DBMS 로 자동 저장 시작, 기준 = 빈 원본이라 「이 구조로」 탭도 고친 것으로 쓴다),
/// 남겨 둔 것·끝난 인스턴스가 남긴 것은 <see cref="ResumeAsync"/>(이 인스턴스가 쥔 뒤 시작, 탭의 편집 세션은 <see cref="ResumedDraft.Bind"/> 로 묶는다).
/// 끝맺음: 실행 성공·「버리기」 = 지움, 「남겨 두기」 = 써 두고 놓음(못 쓰면 예외). 앱 종료 확인은 <see cref="INewTableDraftLink.SaveNowAsync"/> 로 써 두고(쥔 채),
/// 종료 작업 <see cref="SaveAllAsync"/> 가 한 번 더 쓴 뒤 멈춘다 — 다음 실행의 작업 공간 복원이 되살린다(SQL 탭 종료 자동 저장과 같은 규칙, D-016).
/// </summary>
[OrandaService(typeof(INewTableDraftHook))]
public sealed class TableDraftHook(TableDraftLibrary library) : INewTableDraftHook
{
    private readonly ConcurrentDictionary<Link, byte> _live = new();

    public TableDraftLibrary Library => library;

    /// <summary>끝나지 않은 연결 수(시험·진단용).</summary>
    internal int LiveCount => _live.Count;

    public INewTableDraftLink? Attach(ISession session, TableEditSession edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        // 기준은 빈 원본: 탭의 「고친 것」(원본과 다름) 판정과 같게 — 고치지 않은 「이 구조로」 탭도 남겨 두기·종료 때 쓴다.
        return Track(library.Begin(session.Profile.Name, session.Profile.Dbms, edit.Original, () => edit.Draft));
    }

    /// <summary>
    /// 저장분을 다시 연다: 이 인스턴스가 쥐고 자동 저장을 시작한다. 그 사이 다른 탭·다른 앱이 쥐었거나 지워졌으면 null.
    /// 돌려준 연결의 지금 모델은 <see cref="ResumedDraft.Bind"/> 로 탭의 편집 세션을 묶기 전까지 저장분 그대로다(쓰지 않는다).
    /// </summary>
    public async Task<ResumedDraft?> ResumeAsync(StoredTableDraft stored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var bound = new ResumedDraft(stored.Draft);
        if (await library.ResumeAsync(stored, bound.Snapshot, ct).ConfigureAwait(true) is not { } autosave)
        {
            return null;
        }

        bound.Link = Track(autosave);
        return bound;
    }

    /// <summary>앱 종료 작업: 열린 새 테이블 탭마다 바뀐 것을 써 두고 멈춘다(이 인스턴스가 쥔 채 — 다음 실행이 되살린다).</summary>
    public async Task SaveAllAsync(CancellationToken ct)
    {
        foreach (var link in _live.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await link.DisposeAsync().ConfigureAwait(true); // 써 두고 멈춘다(실패해도 마지막 자동 저장분이 남는다 — 종료 확인이 이미 써 뒀다)
        }
    }

    private Link Track(TableDraftAutosave autosave)
    {
        var link = new Link(this, autosave);
        _live[link] = 0;
        return link;
    }

    private sealed class Link(TableDraftHook owner, TableDraftAutosave autosave) : INewTableDraftLink
    {
        public async Task FinishAsync(NewTableDraftEnd reason, CancellationToken ct)
        {
            if (autosave.IsEnded)
            {
                owner._live.TryRemove(this, out _);
                return;
            }

            if (reason == NewTableDraftEnd.Kept)
            {
                await autosave.SaveIfChangedAsync(ct).ConfigureAwait(true);
                if (autosave.HasUnsavedChanges)
                {
                    // 저장소에 쓸 수 없다(마이그레이션 실패·더 새로운 버전): 남겨 뒀다고 믿게 두지 않는다.
                    throw new InvalidOperationException(TableEditText.T("작업 저장소에 쓸 수 없습니다", "The draft store cannot be written"));
                }

                await autosave.CloseAsync(keep: true, ct).ConfigureAwait(true);
            }
            else
            {
                await autosave.DiscardAsync(ct).ConfigureAwait(true);
            }

            owner._live.TryRemove(this, out _); // 저장소가 실패하면 예외가 나가고 연결은 남는다(다시 끝맺을 수 있다)
        }

        public async Task<bool> SaveNowAsync(CancellationToken ct)
        {
            if (autosave.IsEnded)
            {
                return false;
            }

            await autosave.SaveIfChangedAsync(ct).ConfigureAwait(true);
            return !autosave.HasUnsavedChanges;
        }

        /// <summary>끝맺지 않는다: 바뀐 것을 한 번 써 두고 자동 저장을 멈춘다(저장분은 쥔 채 — 다음 실행이 되살린다).</summary>
        public async ValueTask DisposeAsync()
        {
            owner._live.TryRemove(this, out _);
            try
            {
                await autosave.SaveIfChangedAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 못 쓰면 마지막 자동 저장분이 남는다.
            }

            await autosave.DisposeAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>
/// 다시 연 저장분(<see cref="TableDraftHook.ResumeAsync"/>). 자동 저장이 읽는 지금 모델은 <see cref="Bind"/> 한 편집 세션의 것이다 —
/// 탭의 VM 상태(나중에 다른 편집으로 바뀔 수 있다)를 읽지 않고 그 세션 하나에 묶는다. 묶기 전에는 저장분 그대로(쓰지 않음).
/// </summary>
public sealed class ResumedDraft(TableDraft stored)
{
    private TableEditSession? _edit;

    /// <summary>작업 저장 연결.</summary>
    public INewTableDraftLink Link { get; internal set; } = null!;

    /// <summary>탭의 편집 세션을 묶는다(한 번만 — 두 번째부터 무시).</summary>
    public void Bind(TableEditSession edit) => Interlocked.CompareExchange(ref _edit, edit, null);

    /// <summary>지금 모델(타이머 스레드에서도 불린다).</summary>
    internal TableDraft Snapshot() => Volatile.Read(ref _edit)?.Draft ?? stored;
}
