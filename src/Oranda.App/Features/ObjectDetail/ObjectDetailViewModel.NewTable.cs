using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>작업 저장에 붙은 새 테이블 탭을 닫을 때의 답(설계 18.7 「버리기 / 남겨 두기 / 취소」).</summary>
public enum NewTableCloseChoice
{
    Cancel,
    Discard,
    Keep,
}

/// <summary>새 테이블 「종류」 칸 한 줄(설계 18.10). <see cref="Reason"/> 가 있으면 이 서버에서 못 만들어 흐리게 둔다(설계 18.8).</summary>
/// <param name="PreserveRows">임시 테이블의 <c>ON COMMIT PRESERVE ROWS</c>(거짓 = DELETE ROWS)</param>
/// <param name="Reason">못 만드는 이유 문구 키(<see cref="ITableEditDialect.UnsupportedReasonKey"/>). null = 만들 수 있다</param>
public sealed record NewTableKindOption(TableKind Kind, bool PreserveRows, string Text, string? Reason)
{
    public bool IsEnabled => Reason is null;

    /// <summary>흐린 줄의 풍선 도움말.</summary>
    public string? ReasonText => Reason is null ? null : Localization.UiText.Get(Reason, Reason);

    public override string ToString() => Text;
}

/// <summary>
/// 「✎ 새 테이블」 탭(테이블 편집기 설계 7.5·18.10, 시안 화면 ①, T8). 빈 객체 상세가 처음부터 편집 모드로 열린다: 원본은 빈 모델(<see cref="TableDraftFactory.Empty"/>),
/// 페이지는 열·제약·인덱스만 켜지고(데이터·통계·크기는 흐림), ✎ 토글은 끌 수 없다. 위 칸(소유자·이름·주석·종류)이 편집 모델을 바꾼다 — 이름을 고치면 규칙 이름
/// 제약·인덱스가 따라 바뀐다(<see cref="TableEditSession.SetTableName"/>, 손으로 고친 이름 제외). 실행이 테이블을 만들면 스키마 브라우저 목록을 다시 받고 같은 탭이 그 테이블 상세가 된다.
/// 편집 화면·실행은 기존 테이블 편집(<see cref="TableEditSession"/>·<see cref="TableEditRunner"/>)을 그대로 쓴다.
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    /// <summary>「✎ 새 테이블」 탭 머리 이름.</summary>
    public static string NewTableTitle => TableEditText.T("✎ 새 테이블", "✎ New table");

    private string _newTableNameText = string.Empty;
    private INewTableDraftLink? _draftLink;
    private TableEditSession? _newTableHooked;
    private bool _newTableClosed;

    /// <summary>새 테이블 탭이다(아직 서버에 테이블이 없다). 실행이 테이블을 만들면 거짓.</summary>
    [ObservableProperty]
    private bool _isNewTableMode;

    /// <summary>소유자 칸 목록(스키마 브라우저의 스키마, 고른 소유자는 늘 들어 있다).</summary>
    public IReadOnlyList<string> NewTableOwners { get; private set; } = [];

    /// <summary>「종류」 칸 목록(일반·임시 DELETE/PRESERVE·IOT, 설계 18.10).</summary>
    public IReadOnlyList<NewTableKindOption> NewTableKinds { get; private set; } = [];

    /// <summary>위 칸(소유자·이름·주석·종류)이 보인다.</summary>
    public bool ShowsNewTableHeader => IsNewTableMode && TableEdit is not null;

    /// <summary>✎ 토글을 누를 수 있다(새 테이블 탭은 끌 수 없다, 설계 7.5).</summary>
    public bool CanUseTableEditToggle => CanEditTable && !IsNewTableMode;

    /// <summary>위 칸을 고칠 수 있다(새 테이블 탭이고 실행 중이 아님 — 실행 문장은 시작 때 잡은 것이다).</summary>
    private bool CanEditNewTable => IsNewTableMode && !IsTableEditRunning;

    /// <summary>「새로 고침」 단추(새 테이블 탭은 읽을 것이 없다).</summary>
    public bool CanRefreshDetail => CanToggleTableEdit && !IsNewTableMode;

    /// <summary>소유자 칸. 목록에 없는 값은 받지 않는다.</summary>
    public string? NewTableOwner
    {
        get => IsNewTableMode ? TableEdit?.Draft.Owner : null;
        set
        {
            if (CanEditNewTable && TableEdit is { } edit && value is { Length: > 0 } owner && NewTableOwners.Contains(owner, StringComparer.Ordinal)
                && !string.Equals(edit.Draft.Owner, owner, StringComparison.Ordinal))
            {
                edit.Update(d => d with { Owner = owner });
            }

            RaiseNewTableHeader();
        }
    }

    /// <summary>
    /// 이름 칸. 칸은 쓴 글 그대로 두고(쓰는 중에 대문자로 바뀌어 커서가 튀지 않게), 모델 이름은 따옴표 없는 글이면 대문자(<see cref="TableEditNames.FromInput"/>).
    /// </summary>
    public string NewTableName
    {
        get => _newTableNameText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_newTableNameText, value, StringComparison.Ordinal))
            {
                return;
            }

            if (IsNewTableMode && !CanEditNewTable)
            {
                OnPropertyChanged(); // 실행 중에는 받지 않는다 — 칸을 모델 이름으로 되돌린다
                return;
            }

            _newTableNameText = value;
            if (CanEditNewTable && TableEdit is { } edit)
            {
                var name = TableEditCellText.Parse(value);
                if (!string.Equals(edit.Draft.Name, name, StringComparison.Ordinal))
                {
                    edit.SetTableName(name);
                }
            }

            OnPropertyChanged();
            RaiseNewTableHeader();
        }
    }

    /// <summary>주석 칸(테이블 주석).</summary>
    public string? NewTableComment
    {
        get => IsNewTableMode ? TableEdit?.TableComment : null;
        set
        {
            if (CanEditNewTable && TableEdit is { } edit)
            {
                edit.TableComment = value;
            }
        }
    }

    /// <summary>「종류」 칸. 흐린 줄(이 서버에서 못 만듦)은 받지 않는다.</summary>
    public NewTableKindOption? SelectedNewTableKind
    {
        get => TableEdit is { Draft: var d } && IsNewTableMode
            ? NewTableKinds.FirstOrDefault(k => k.Kind == d.Kind && (d.Kind != TableKind.Temporary || k.PreserveRows == d.OnCommitPreserveRows))
            : null;
        set
        {
            if (CanEditNewTable && TableEdit is { } edit && value is { IsEnabled: true } kind)
            {
                edit.Update(d => d with { Kind = kind.Kind, OnCommitPreserveRows = kind.Kind == TableKind.Temporary && kind.PreserveRows });
            }

            OnPropertyChanged();
        }
    }

    /// <summary>만들 테이블(이름이 있을 때). 실행·검사·가상 식 미리보기의 대상.</summary>
    public DbObjectRef? NewTableTarget => IsNewTableMode && TableEdit is { Draft: { Name.Length: > 0 } d } ? new DbObjectRef(d.Owner, d.Name, "TABLE") : null;

    /// <summary>시험용: 스키마 브라우저 목록 다시 받기(기본 = 같은 접속이면 스키마 브라우저 범주 새로 고침, 아니면 메타데이터 목록만).</summary>
    internal Func<ISession, string, CancellationToken, Task>? RefreshSchemaTables { get; set; }

    /// <summary>시험용: 작업 저장 연결(기본 = 서비스의 <see cref="INewTableDraftHook"/> = <see cref="TableDraftHook"/>).</summary>
    internal INewTableDraftHook? NewTableDraftHook { get; set; }

    /// <summary>
    /// 새 테이블 탭으로 연다. <paramref name="copyFrom"/> 이 있으면 그 테이블을 새로 읽어(캐시 무시, TE-4) 구조를 복사하고(<see cref="TableDraftFactory.CopyStructure"/>,
    /// 이름 = 원본 + <c>_COPY</c>), <paramref name="restore"/> 가 있으면 남겨 둔 작업(설계 18.7)을 그대로 연다. 편집을 시작했으면 true.
    /// </summary>
    /// <param name="owner">소유자 칸 기본값(고른 노드의 소유자)</param>
    /// <param name="owners">소유자 칸 목록(스키마 브라우저의 스키마). <paramref name="owner"/> 는 늘 들어간다</param>
    /// <param name="resumed">되살린 저장분(이미 이 인스턴스가 쥠, <see cref="TableDraftHook.ResumeAsync"/>) — 만든 편집 세션을 묶는다. 없으면 새로 붙인다
    /// (<see cref="INewTableDraftHook.Attach"/>). 편집을 시작하지 못하면 놓는다(저장분은 그대로 — 목록에 다시 보인다).</param>
    public async Task<bool> OpenNewTableAsync(ISession session, string owner, IReadOnlyList<string>? owners = null,
                                              DbObjectRef? copyFrom = null, TableDraft? restore = null, ResumedDraft? resumed = null,
                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var started = await OpenNewTableCoreAsync(session, owner, owners, copyFrom, restore, resumed, ct);
        if (!started && resumed is not null)
        {
            await ReleaseAsync(resumed.Link); // 편집을 시작하지 못했다: 써 두고 놓는다(목록에 다시 보인다)
        }

        return started;
    }

    private async Task<bool> OpenNewTableCoreAsync(ISession session, string owner, IReadOnlyList<string>? owners,
                                                   DbObjectRef? copyFrom, TableDraft? restore, ResumedDraft? resumed, CancellationToken ct)
    {
        ReleaseDraftLink();
        EndTableEdit();
        _session = session;
        Target = null;
        SourceTarget = null;
        IsDdlSelected = false;
        DdlText = string.Empty;
        Sections.Clear();
        SelectedSection = null;
        _keyCache.Clear();
        ResetKeyColumns(null);
        var gen = Interlocked.Increment(ref _generation);
        _newTableClosed = false;
        Title = NewTableTitle;
        _newTableNameText = string.Empty;
        IsNewTableMode = true;

        var startOwner = restore?.Owner ?? owner;
        var list = (owners ?? []).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.Ordinal).ToList();
        if (!list.Contains(startOwner, StringComparer.Ordinal))
        {
            list.Add(startOwner);
        }

        NewTableOwners = list;
        OnPropertyChanged(nameof(NewTableOwners));

        var dialect = TableEditDialect ?? DbmsServices.For<ITableEditDialect>(_services, session);
        if (dialect is null)
        {
            StatusText = TableEditText.T($"이 DBMS({session.Profile.Dbms}) 용 테이블 편집 구현이 없다", $"No table editor for this DBMS ({session.Profile.Dbms})");
            return false;
        }

        var engine = TableEditEngine ?? CoreTableEditEngine.Instance;
        var maxBytes = dialect.MaxIdentifierBytes(session.Server);
        TableDraft draft;
        if (restore is not null)
        {
            draft = restore with { IsNewTable = true };
        }
        else if (copyFrom is not null)
        {
            if (ResolveMeta() is not { } meta)
            {
                StatusText = TableEditText.T("메타데이터 제공자가 없다", "No metadata provider");
                return false;
            }

            IsBusy = true;
            try
            {
                var table = await ReadTableFreshAsync(meta, session, copyFrom, ct);
                draft = TableDraftFactory.CopyStructure(table, owner, CopyName(copyFrom.Name, maxBytes), dialect, maxBytes);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText = TableEditText.T($"{copyFrom.Owner}.{copyFrom.Name} 구조를 읽지 못했다: ", $"Could not read the structure of {copyFrom.Owner}.{copyFrom.Name}: ") + ex.Message;
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }
        else
        {
            draft = engine.Empty(owner);
        }

        if (_newTableClosed || gen != _generation)
        {
            return false; // 원본을 읽는 사이 탭을 닫았거나 다른 것을 열었다
        }

        NewTableKinds = KindOptions(dialect, session.Server);
        OnPropertyChanged(nameof(NewTableKinds));
        _newTableNameText = draft.Name.Length == 0 ? string.Empty : TableEditCellText.Show(draft.Name);
        var context = new TableEditContext(maxBytes, session.Profile.Environment, session.Server);
        TableEdit = new TableEditSession(engine.Empty(draft.Owner), draft, dialect, context, engine);

        foreach (var title in new[] { "열", "제약", "인덱스" })
        {
            Sections.Add(new ObjectDetailSection(new DetailSection(title, [], [])));
        }

        foreach (var title in new[] { ObjectDataViewModel.SectionTitle, "통계", "크기" })
        {
            Sections.Add(new ObjectDetailSection(new DetailSection(title, [], [])) { IsAvailable = false });
        }

        SelectedSection = Sections[0];
        OnPropertyChanged(nameof(NewTableName));
        RaiseNewTableHeader();
        StatusText = TableEditText.T("새 테이블 — 이름과 열을 쓰면 아래에 CREATE TABLE 이 보입니다", "New table — fill in the name and columns to see CREATE TABLE below");
        if (resumed is not null)
        {
            resumed.Bind(TableEdit); // 자동 저장은 이 편집 세션 하나를 읽는다(탭이 나중에 다른 편집으로 바뀌어도)
            _draftLink = resumed.Link;
        }
        else
        {
            _draftLink = DraftHook?.Attach(session, TableEdit);
        }

        return true;
    }

    private INewTableDraftHook? DraftHook => NewTableDraftHook ?? _services?.GetService(typeof(INewTableDraftHook)) as INewTableDraftHook;

    /// <summary>작업 저장에 붙어 있다(닫기가 3갈래 「버리기 / 남겨 두기 / 취소」 다, 설계 18.7).</summary>
    internal bool HasDraftLink => IsNewTableMode && _draftLink is not null;

    /// <summary>시험용: 닫기 3갈래 묻기(기본 = 편집기 대화상자 단추 셋).</summary>
    internal Func<string, CancellationToken, Task<NewTableCloseChoice>>? AskCloseNewTable { get; set; }

    /// <summary>
    /// 탭이 닫혔다(<c>DocumentClosed</c>). 닫기 확인이 이미 끝맺었으면 할 일이 없다. 확인 없이 닫혔으면 지우지 않고 써 두고 놓는다 —
    /// 말없이 잃지 않는다(소유자 노드 「만들던 새 테이블…」에 보인다). 앱 종료 중이면 종료 작업이 먼저 멈춰 두어 쥔 채 남는다(다음 실행이 되살린다).
    /// </summary>
    internal void EndNewTableDraft()
    {
        _newTableClosed = true;
        ReleaseDraftLink();
    }

    /// <summary>끝맺지 않고 연결을 놓는다(뒤에서 써 두고 놓음, 「남겨 두기」와 같다).</summary>
    private void ReleaseDraftLink()
    {
        var link = _draftLink;
        _draftLink = null;
        if (link is not null)
        {
            _ = ReleaseAsync(link);
        }
    }

    /// <summary>써 두고 놓는다. 못 하면(쓸 수 없는 저장소 등) 멈추기만 한다(쥔 채 — 다음 실행이 되살린다). 예외를 내지 않는다.</summary>
    private static async Task ReleaseAsync(INewTableDraftLink link)
    {
        try
        {
            await link.FinishAsync(NewTableDraftEnd.Kept, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                await link.DisposeAsync();
            }
            catch (Exception inner) when (inner is not OutOfMemoryException)
            {
                // 마지막 자동 저장분이 남는다.
            }
        }
    }

    /// <summary>연결을 끝맺는다(실행 성공·버리기 = 지움, 남겨 두기 = 써 두고 놓음). 저장소가 실패하면 예외를 내고 연결은 그대로다.</summary>
    private async Task FinishDraftLinkAsync(NewTableDraftEnd end, CancellationToken ct)
    {
        if (_draftLink is not { } link)
        {
            return;
        }

        await link.FinishAsync(end, ct);
        if (ReferenceEquals(_draftLink, link))
        {
            _draftLink = null;
        }
    }

    /// <summary>
    /// 작업 저장에 붙은 새 테이블 탭의 닫기 확인(설계 18.7).
    /// <list type="bullet">
    /// <item>앱 종료: 묻지 않고 지금 써 둔다(쥔 채 — 다음 실행이 되살린다, SQL 탭 종료 자동 저장과 같은 규칙 D-016). 부작용이 지우기가 아니라 쓰기라
    /// 뒤의 종료 확인이 취소돼도 해가 없다. 쓰지 못하면(저장소 실패·쓸 수 없음) 예전처럼 버릴지 묻는다 — 말없이 잃지 않는다.</item>
    /// <item>탭 닫기: 고친 것이 없으면 묻지 않고 지운다. 고쳤으면 「버리기 / 남겨 두기 / 취소」. 저장소가 실패하거나 남겨 둘 수 없으면 알리고 탭을 남긴다.</item>
    /// </list>
    /// </summary>
    private Func<CancellationToken, Task<bool>>? NewTableCloseConfirmation(bool appExit)
    {
        var dirty = TableEdit is { IsDirty: true };
        if (appExit)
        {
            if (!dirty || _draftLink is not { } link)
            {
                return null;
            }

            return async ct =>
            {
                try
                {
                    if (await link.SaveNowAsync(ct))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    StatusText = TableEditText.T("작업을 써 두지 못했다: ", "Could not save the draft: ") + ex.Message;
                }

                return await ConfirmDiscardTableEditAsync(TableEditText.T(
                    "만들던 새 테이블을 작업 저장소에 써 두지 못했습니다. 버리고 끝낼까요?",
                    "The new table could not be saved as a draft. Discard it and exit?"), ct);
            };
        }

        return async ct =>
        {
            var end = NewTableDraftEnd.Discarded;
            if (dirty)
            {
                var name = TableEdit is { Draft.Name.Length: > 0 } e ? $"{e.Draft.Owner}.{e.Draft.Name}" : NewTableTitle;
                var choice = await (AskCloseNewTable ?? AskCloseNewTableDefaultAsync)(name, ct);
                if (choice == NewTableCloseChoice.Cancel)
                {
                    return false;
                }

                end = choice == NewTableCloseChoice.Keep ? NewTableDraftEnd.Kept : NewTableDraftEnd.Discarded;
            }

            try
            {
                await FinishDraftLinkAsync(end, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                StatusText = TableEditText.T("작업 저장을 끝맺지 못해 탭을 남겼다: ", "Could not finish the saved draft; the tab stays open: ") + ex.Message;
                return false;
            }
        };
    }

    private static async Task<NewTableCloseChoice> AskCloseNewTableDefaultAsync(string name, CancellationToken ct)
    {
        var index = await SqlEditor.SqlEditorHost.Current.Dialogs.AskButtonsAsync(
            name,
            TableEditText.T("만들던 새 테이블을 어떻게 할까요?\n「남겨 두기」 한 것은 소유자 노드의 「새 테이블 ▸ 만들던 새 테이블…」로 다시 엽니다.",
                            "What should happen to this new table?\nA kept one opens again from the owner node: New Table ▸ Resume New Table…"),
            [TableEditText.T("버리기", "Discard"), TableEditText.T("남겨 두기", "Keep"), TableEditText.T("취소", "Cancel")],
            defaultIndex: 1,
            ct);
        return index switch
        {
            0 => NewTableCloseChoice.Discard,
            1 => NewTableCloseChoice.Keep,
            _ => NewTableCloseChoice.Cancel,
        };
    }

    /// <summary>
    /// 새 테이블 실행이 문장을 하나 이상 반영했다(첫 문장 = <c>CREATE TABLE</c> 이라 테이블이 생겼다): 스키마 목록을 다시 받고 같은 탭을 그 테이블 상세로 연다.
    /// 다 됐으면 편집을 끄고, 중간 실패면 만든 테이블을 다시 읽어 남은 편집만 그 위에 둔다(TE-5).
    /// </summary>
    private async Task<string?> AfterNewTableAppliedAsync(ISession session, DbObjectRef target, TableEditSession edit, TableEditRunResult result, CancellationToken ct)
    {
        string? note = null;
        try
        {
            await FinishDraftLinkAsync(NewTableDraftEnd.Created, CancellationToken.None); // 만들었으니 남긴 작업을 지운다(설계 18.7)
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReleaseDraftLink(); // 지우지 못했으면 놓는다 — 「만들던 새 테이블…」 목록에 보여 거기서 지운다
            note = TableEditText.T("남긴 작업을 지우지 못했다(「새 테이블 ▸ 만들던 새 테이블…」에서 지우세요): ",
                                   "could not delete the saved draft (delete it from New Table ▸ Resume New Table…): ") + ex.Message;
        }

        try
        {
            await InvalidateAsync(session, target, ct);
            await RefreshSchemaTablesAsync(session, target.Owner, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            note = Join(note, TableEditText.T("스키마 브라우저 목록을 다시 받지 못했다: ", "could not refresh the schema browser: ") + ex.Message);
        }

        IsNewTableMode = false;
        var meta = ResolveMeta();
        if (result.Outcome == TableEditRunOutcome.Succeeded || meta is null)
        {
            EndTableEdit();
            return await OpenCreatedAsync(session, target, ct) is { } error ? Join(note, error) : note;
        }

        try
        {
            var table = await ReadTableFreshAsync(meta, session, target, ct);
            var newOriginal = edit.Engine.FromTable(table, edit.Dialect);
            var remaining = TableDraftRebase.Rebase(edit.Original, edit.Draft, result.Script, result.SucceededCount, newOriginal);
            var error = await OpenCreatedAsync(session, target, ct); // 다시 열기는 편집을 끈다 — 뒤에서 남은 편집으로 다시 켠다
            edit.Reset(newOriginal, remaining);
            TableEdit = edit;
            return Join(note, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 테이블은 생겼는데 지금 구조를 모른다: 남은 문장을 계산하지 않고 편집을 끈다(반영된 CREATE 를 다시 보내지 않게).
            EndTableEdit();
            var error = await OpenCreatedAsync(session, target, ct);
            return Join(note, Join(TableEditText.T("만든 테이블을 다시 읽지 못해 남은 편집을 버렸다: ", "could not reread the new table; the remaining edits were dropped: ") + ex.Message, error));
        }
    }

    /// <summary>같은 탭을 만든 테이블 상세로 연다. 실패하면 그 이유.</summary>
    private async Task<string?> OpenCreatedAsync(ISession session, DbObjectRef target, CancellationToken ct)
    {
        try
        {
            if (ResolveMeta() is not null)
            {
                await OpenAsync(session, target, showDdl: false, ct);
            }
            else
            {
                Target = target;
                Title = $"{target.Owner}.{target.Name}";
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Target = target;
            Title = $"{target.Owner}.{target.Name}";
            return TableEditText.T("상세를 다시 읽지 못했다: ", "could not reload the detail: ") + ex.Message;
        }
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " · " + b;

    /// <summary>새 테이블이 스키마 브라우저에 보이게 그 소유자의 테이블 목록을 서버에서 다시 받는다.</summary>
    private async Task RefreshSchemaTablesAsync(ISession session, string owner, CancellationToken ct)
    {
        if (RefreshSchemaTables is { } refresh)
        {
            await refresh(session, owner, ct);
            return;
        }

        var browser = ObjectDetailCommandHandler.FindSchemaBrowser(_services);
        if (browser?.CurrentSession is { } shown && string.Equals(shown.DbKey, session.DbKey, StringComparison.Ordinal))
        {
            await browser.RefreshObjectTypeAsync(owner, "TABLE", ct);
        }
        else if (ResolveMeta() is { } meta)
        {
            await meta.RefreshObjectTypeAsync(session, owner, "TABLE", ct);
        }
    }

    /// <summary>「이 구조로」 새 이름: 원본 + <c>_COPY</c>(바이트 한도를 넘으면 원본 쪽을 줄인다).</summary>
    internal static string CopyName(string source, int maxBytes)
    {
        const string suffix = "_COPY";
        var head = source;
        while (head.Length > 1 && Encoding.UTF8.GetByteCount(head) + suffix.Length > maxBytes)
        {
            head = head[..^1];
        }

        return head + suffix;
    }

    private static IReadOnlyList<NewTableKindOption> KindOptions(ITableEditDialect dialect, ServerInfo? server)
    {
        var temporary = dialect.UnsupportedReasonKey(TableEditFeature.TemporaryTable, server);
        return
        [
            new(TableKind.Heap, false, TableEditText.T("일반", "Heap"), null),
            new(TableKind.Temporary, false, TableEditText.T("임시 — 커밋하면 행 지움", "Temporary — delete rows on commit"), temporary),
            new(TableKind.Temporary, true, TableEditText.T("임시 — 세션 끝까지 행 유지", "Temporary — keep rows for the session"), temporary),
            new(TableKind.Iot, false, TableEditText.T("인덱스 구성(IOT)", "Index-organized (IOT)"), dialect.UnsupportedReasonKey(TableEditFeature.IndexOrganizedTable, server)),
        ];
    }

    private void RaiseNewTableHeader()
    {
        OnPropertyChanged(nameof(NewTableOwner));
        OnPropertyChanged(nameof(NewTableComment));
        OnPropertyChanged(nameof(SelectedNewTableKind));
        OnPropertyChanged(nameof(NewTableTarget));
        OnPropertyChanged(nameof(ShowsNewTableHeader));
    }

    partial void OnIsNewTableModeChanged(bool value)
    {
        if (!value)
        {
            ReleaseDraftLink(); // 만들지 않고 다른 객체를 열었다: 써 두고 놓는다(만들었으면 이미 Created 로 끝났다)
        }

        RaiseTableEditProperties();
        OnPropertyChanged(nameof(CanUseTableEditToggle));
        OnPropertyChanged(nameof(CanRefreshDetail));
        RaiseNewTableHeader();
    }

    /// <summary>흐린 페이지(새 테이블 탭의 데이터·통계·크기)는 고르지 않는다 — 앞 페이지로 되돌린다.</summary>
    partial void OnSelectedSectionChanged(ObjectDetailSection? oldValue, ObjectDetailSection? newValue)
    {
        if (newValue is { IsAvailable: false })
        {
            SelectedSection = oldValue is { IsAvailable: true } && Sections.Contains(oldValue) ? oldValue : Sections.FirstOrDefault(s => s.IsAvailable);
        }
    }

    /// <summary>편집 상태에 매인 새 테이블 칸·단추 속성을 함께 알린다(편집 파일을 건드리지 않고 여기서 잇는다).</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(CanEditTable):
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanUseTableEditToggle)));
                break;
            case nameof(CanToggleTableEdit):
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanRefreshDetail)));
                break;
            case nameof(TableEdit):
                HookNewTableEdit();
                RaiseNewTableHeader();
                break;
        }
    }

    private void HookNewTableEdit()
    {
        if (_newTableHooked is { } old)
        {
            old.Recomputed -= OnNewTableRecomputed;
        }

        _newTableHooked = TableEdit;
        if (_newTableHooked is { } now)
        {
            now.Recomputed += OnNewTableRecomputed;
        }
    }

    private void OnNewTableRecomputed(object? sender, EventArgs e)
    {
        if (IsNewTableMode)
        {
            // 모델 이름이 칸 글과 달라졌으면(되돌리기 등 칸 밖에서 바뀜) 칸을 모델 이름으로 맞춘다.
            if (TableEdit is { } edit && !string.Equals(TableEditCellText.Parse(_newTableNameText), edit.Draft.Name, StringComparison.Ordinal))
            {
                _newTableNameText = edit.Draft.Name.Length == 0 ? string.Empty : TableEditCellText.Show(edit.Draft.Name);
                OnPropertyChanged(nameof(NewTableName));
            }

            RaiseNewTableHeader();
        }
    }
}
