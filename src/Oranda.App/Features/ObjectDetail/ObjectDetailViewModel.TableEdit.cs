using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 객체 상세에서 바로 고치기(테이블 편집기 설계 6.1 A·7.1). 「열」「제약」「인덱스」「설명」 페이지의 ✎ 편집 토글이 켜면, 캐시를 무효화하고 새로 읽은
/// <see cref="TableInfo"/>(TE-4·TE-28)로 편집 세션(<see cref="TableEdit"/>)을 만든다. 세션은 탭 단위라 페이지를 오가도 남고, 끄기·새로 고침·닫기·
/// 열 순서 바꾸기 앞에서 고친 내용이 있으면 버릴지 묻는다(설계 4장). 실행(T7)은 <c>ObjectDetailViewModel.TableEditRun.cs</c>.
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    /// <summary>편집 모드의 세션. 끄면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableEditing), nameof(IsEditPageShown))]
    private TableEditSession? _tableEdit;

    /// <summary>편집 모드가 켜져 있다.</summary>
    public bool IsTableEditing => TableEdit is not null;

    /// <summary>지금 페이지가 편집 페이지(열·제약·인덱스·설명)면 그 종류.</summary>
    public TableEditPage? EditPage => SelectedSection?.Title switch
    {
        "열" => TableEditPage.Columns,
        "제약" => TableEditPage.Constraints,
        "인덱스" => TableEditPage.Indexes,
        "설명" => TableEditPage.Description,
        PartitionsTitle => TableEditPage.Partitions,
        StorageTitle => TableEditPage.Storage,
        _ => null,
    };

    /// <summary>「저장」 구역 이름(공급자는 만들지 않는다 — 편집 중에만 끼운다, 설계 18.2).</summary>
    internal const string StorageTitle = "저장";

    /// <summary>「파티션」 구역 이름(공급자가 파티션 테이블에만 만든다 — 편집 중에는 일반 테이블에도 빈 구역을 끼워 파티션 테이블로 바꾸는 길을 연다, 설계 18.4).</summary>
    internal const string PartitionsTitle = "파티션";

    /// <summary>✎ 편집 토글이 보인다: 대상이 TABLE 이고 편집 페이지를 보고 있다(설계 7.1, <see cref="CanReorderColumns"/> 와 같은 판정 자리).</summary>
    /// 기능이 꺼져도 편집 중이면 끌 수 있게 토글은 남긴다(기능 게이트가 바뀌어도 고친 내용을 잃지 않게).
    public bool CanToggleTableEdit => (Target is { ObjectType: "TABLE" } || IsNewTableMode) && EditPage is not null && (IsTableEditing || TableEditCommands.IsEnabled(_services));

    private Oranda.Core.Features.IFeatureGate? _watchedGate;

    /// <summary>기능 게이트가 바뀌면(Role·기능 켜고 끄기) ✎ 토글을 다시 알린다(처음 열 때 한 번 구독).</summary>
    private void WatchFeatureGate()
    {
        if (_watchedGate is not null || _services?.GetService(typeof(Oranda.Core.Features.IFeatureGate)) is not Oranda.Core.Features.IFeatureGate gate)
        {
            return;
        }

        _watchedGate = gate;
        gate.Changed += OnFeatureGateChanged;
    }

    private void OnFeatureGateChanged(object? sender, EventArgs e) => Oranda.App.Shell.UiThread.PostIfAppRunning(() =>
    {
        UpdateCanReorderColumns(); // 「열 순서 바꾸기…」 단추도 기능 columnReorder 를 따른다
        RaiseTableEditProperties();
    });

    /// <summary>문서를 닫을 때 게이트 구독을 푼다(닫힌 탭이 남지 않게).</summary>
    internal void DetachFeatureGate()
    {
        if (_watchedGate is { } gate)
        {
            gate.Changed -= OnFeatureGateChanged;
            _watchedGate = null;
        }
    }

    /// <summary>편집 표가 읽기 표 대신 보인다(편집 중이고 편집 페이지).</summary>
    public bool IsEditPageShown => IsTableEditing && EditPage is not null;

    /// <summary>위 도구줄(✎ 편집·열 순서 바꾸기)이 보인다.</summary>
    public bool ShowsTableToolbar => CanToggleTableEdit || CanReorderColumns;

    /// <summary>읽기 표(구역 표)가 보인다(데이터 탭도 편집 표도 아님).</summary>
    public bool ShowsSectionTable => !IsDataSelected && !IsEditPageShown;

    public bool IsEditingColumns => IsEditPageShown && EditPage == TableEditPage.Columns;

    public bool IsEditingConstraints => IsEditPageShown && EditPage == TableEditPage.Constraints;

    public bool IsEditingDescription => IsEditPageShown && EditPage == TableEditPage.Description;

    public bool IsEditingPartitions => IsEditPageShown && EditPage == TableEditPage.Partitions;

    public bool IsEditingStorage => IsEditPageShown && EditPage == TableEditPage.Storage;

    /// <summary>위 도구줄의 줄 단추(＋ － ↑↓ ↺): 열·제약·인덱스·설명 페이지. 파티션·저장 페이지는 그 화면의 칸을 쓴다.</summary>
    public bool ShowsRowToolbar => IsEditPageShown && !IsEditingPartitions && !IsEditingStorage;

    /// <summary>「＋」「－」을 쓸 수 있다(편집 페이지가 줄 표이고 제한된 테이블이 아님).</summary>
    public bool CanAddRow => TableEdit is { } edit && EditPage is { } page && edit.CanAdd(page);

    public bool IsDevelopmentConnection => _session?.Profile.Environment == Oranda.Core.Connection.ConnectionEnvironment.Development;

    public bool IsProductionConnection => _session?.Profile.Environment == Oranda.Core.Connection.ConnectionEnvironment.Production;

    /// <summary>아래 줄 환경 배지 글(개발/테스트/운영).</summary>
    public string EnvironmentText => _session?.Profile.Environment switch
    {
        Oranda.Core.Connection.ConnectionEnvironment.Production => TableEditText.T("운영", "Production"),
        Oranda.Core.Connection.ConnectionEnvironment.Test => TableEditText.T("테스트", "Test"),
        _ => TableEditText.T("개발", "Development"),
    };

    /// <summary>「스크립트 저장…」·복사·편집기로 열기: 문장이 있다(✖ 가 있어도 저장은 된다, TE-19).</summary>
    public bool CanSaveTableEditScript => TableEdit is { ScriptError: null, Script.IsEmpty: false };

    /// <summary>시험용: 저장 파일 고르기(기본 = 편집기 대화상자의 저장 창). null = 취소.</summary>
    internal Func<string, CancellationToken, Task<string?>>? PickScriptFile { get; set; }

    /// <summary>시험용: 「편집기로 열기」(기본 = 새 SQL 탭).</summary>
    internal Func<string, Guid?, bool>? DdlEditorOpener { get; set; }

    /// <summary>시험용: 오늘 날짜(스크립트 머리 주석).</summary>
    internal Func<DateOnly>? Today { get; set; }

    /// <summary>
    /// 「스크립트 저장…」(설계 7.7·9.4): 이 편집의 문장만 의뢰용 형식(<see cref="RequestScriptWriter"/>, 머리 = 테이블 이름)으로 <c>.sql</c> 에 쓴다(UTF-8 BOM 없음, TE-17).
    /// 저장했으면 그 경로.
    /// </summary>
    public async Task<string?> SaveTableEditScriptAsync(CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (TableEdit is not { } edit || !CanSaveTableEditScript || _session is not { } session)
        {
            return null;
        }

        var name = edit.Draft.Name.Length > 0 ? edit.Draft.Name : "NEW_TABLE";
        var text = RequestScriptWriter.Write(
            new RequestScriptHeader($"{edit.Draft.Owner}.{name}", Today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now), session.Profile.Dbms),
            edit.Script.SqlStatements, Environment.NewLine);
        var pick = PickScriptFile ?? ((suggested, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PickSaveFileAsync(suggested, token));
        var path = await pick(name + ".sql", ct);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(path, text, RequestScriptWriter.Encoding, ct);
            StatusText = TableEditText.T("스크립트를 저장했다: ", "Saved the script: ") + path;
            return path;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = TableEditText.T("스크립트를 저장하지 못했다: ", "Could not save the script: ") + ex.Message;
            return null;
        }
    }

    /// <summary>「편집기로 열기」(설계 7.7): 미리보기 문장을 새 SQL 탭에 넣는다(이 접속, 실행하지 않음). 열었으면 true.</summary>
    public bool OpenTableEditDdlInEditor()
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (TableEdit is not { } edit || !CanSaveTableEditScript)
        {
            return false;
        }

        var ok = (DdlEditorOpener ?? OpenInNewEditor)(edit.PreviewText + Environment.NewLine, _session?.Profile.Id);
        StatusText = ok ? TableEditText.T("새 SQL 탭에 열었다(실행은 직접 한다)", "Opened in a new SQL tab (run it yourself)") : TableEditText.T("편집기를 열지 못했다", "Could not open the editor");
        return ok;
    }

    /// <summary>시험용: 편집 로직(기본 = Core).</summary>
    internal ITableEditEngine? TableEditEngine { get; set; }

    /// <summary>시험용: 방언(기본 = 세션 DBMS 의 <see cref="ITableEditDialect"/>).</summary>
    internal ITableEditDialect? TableEditDialect { get; set; }

    /// <summary>시험용: 그 테이블의 메타데이터 캐시 무효화(기본 = <see cref="IMetadataCache.InvalidateAsync"/>).</summary>
    internal Func<ISession, DbObjectRef, CancellationToken, Task>? InvalidateTableCache { get; set; }

    /// <summary>
    /// ✎ 편집 켜기·끄기. 켜기 = 캐시 무효화 → <see cref="IMetadataProvider.GetTableAsync"/> → 원본 모델. 방언이 없으면 켜지 않고 이유를 보인다.
    /// 끄기 = 고친 내용이 있으면 버릴지 묻고, 거절하면 켠 채로 둔다. 결과 상태(켜졌나)를 돌려준다.
    /// </summary>
    public async Task<bool> SetTableEditingAsync(bool on, CancellationToken ct = default)
    {
        if (!on)
        {
            if (IsNewTableMode)
            {
                return IsTableEditing; // 새 테이블 탭은 끌 수 없다(설계 7.5)
            }

            if (TableEdit is not null && !await ConfirmDiscardTableEditAsync(
                    TableEditText.T("고친 내용을 버리고 편집을 끌까요?", "Discard your changes and turn editing off?"), ct))
            {
                return true;
            }

            EndTableEdit();
            await ReopenIfStaleAsync(ct); // 중간 실패 뒤 낡은 상세 표를 다시 읽는다
            return false;
        }

        if (TableEdit is not null)
        {
            return true;
        }

        if (Target is not { ObjectType: "TABLE" } target || _session is not { } session || ResolveMeta() is not { } meta || !TableEditCommands.IsEnabled(_services))
        {
            return false;
        }

        var dialect = TableEditDialect ?? DbmsServices.For<ITableEditDialect>(_services, session);
        if (dialect is null)
        {
            StatusText = TableEditText.T($"이 DBMS({session.Profile.Dbms}) 용 테이블 편집 구현이 없다", $"No table editor for this DBMS ({session.Profile.Dbms})");
            return false;
        }

        IsBusy = true;
        try
        {
            var table = await ReadTableFreshAsync(meta, session, target, ct);
            var engine = TableEditEngine ?? CoreTableEditEngine.Instance;
            var original = engine.FromTable(table, dialect);
            var context = new TableEditContext(dialect.MaxIdentifierBytes(session.Server), session.Profile.Environment, session.Server);
            var edit = new TableEditSession(original, dialect, context, engine);
            edit.SetPartitions(table);
            TableEdit = edit;
            EnsurePartitionsSection();
            StatusText = TableEdit.IsRestricted
                ? TableEdit.RestrictionText
                : TableEditText.T("편집 중 — 고친 내용은 아래 DDL 로 보입니다", "Editing — your changes appear as DDL below");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = TableEditText.T("편집을 켜지 못했다: ", "Could not start editing: ") + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 고친 내용이 있으면 <paramref name="question"/> 으로 버릴지 묻는다. 고친 것이 없거나 버리기로 했으면 true(부르는 쪽이 이어서 끄거나 닫는다).
    /// </summary>
    public async Task<bool> ConfirmDiscardTableEditAsync(string question, CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke(); // 칸에 쓰고 아직 벗어나지 않은 글도 고친 내용으로 본다
        if (TableEdit is not { IsDirty: true })
        {
            return true;
        }

        var confirm = Confirm ?? ((message, token) => SqlEditor.SqlEditorHost.Current.Dialogs.ConfirmAsync(message, token));
        return await confirm(question, ct);
    }

    /// <summary>
    /// 「새로 고침」: 편집 중이면 「고친 내용을 버리고 새로 읽을까요?」를 물은 뒤 다시 연다(편집은 꺼진다, 설계 7.1). 다시 열었으면 true.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (_session is not { } session || Target is not { } target)
        {
            return false;
        }

        if (!await ConfirmDiscardTableEditAsync(TableEditText.T("고친 내용을 버리고 새로 읽을까요?", "Discard your changes and reload?"), ct))
        {
            return false;
        }

        await OpenAsync(session, target, IsDdlSelected, ct);
        return true;
    }

    /// <summary>
    /// 「열 순서 바꾸기…」를 열기 전: 고친 내용이 있으면 버릴지 묻고(실행은 아래 「실행」으로 먼저 하라는 안내 포함), 버리면 편집을 끈다(설계 13장). 열어도 되면 true.
    /// </summary>
    public async Task<bool> ConfirmBeforeReorderAsync(CancellationToken ct = default)
    {
        if (!await ConfirmDiscardTableEditAsync(TableEditText.T(
                "고친 내용이 있습니다. 먼저 아래 「실행」으로 반영하거나 버려야 합니다.\n고친 내용을 버리고 열 순서 바꾸기를 열까요?",
                "You have unapplied changes. Run them first with Run below, or discard them.\nDiscard your changes and open Reorder Columns?"), ct))
        {
            return false;
        }

        EndTableEdit();
        return true;
    }

    /// <summary>
    /// 탭을 닫을 때(도킹 <c>CloseConfirm</c>)·앱을 끝낼 때 물을 것. 포커스된 칸 글을 먼저 넣고 본다. 고친 내용이 없으면 null.
    /// </summary>
    /// <param name="appExit">앱 종료 확인(작업 저장에 붙은 새 테이블 탭은 묻지 않는다 — 써 두고 다음 실행이 되살린다, 설계 18.7)</param>
    internal Func<CancellationToken, Task<bool>>? CloseConfirmation(bool appExit = false)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (HasDraftLink)
        {
            return NewTableCloseConfirmation(appExit); // 새 테이블 작업 저장(ObjectDetailViewModel.NewTable.cs)
        }

        return TableEdit is { IsDirty: true } edit
            ? ct => ConfirmDiscardTableEditAsync(edit.IsNewTable
                ? TableEditText.T("만들던 새 테이블을 버리고 탭을 닫을까요?", "Discard the new table and close the tab?")
                : TableEditText.T("고친 내용을 버리고 탭을 닫을까요?", "Discard your changes and close the tab?"), ct)
            : null;
    }

    /// <summary>편집 모드를 묻지 않고 끈다(다시 열기·버리기로 정한 뒤).</summary>
    internal void EndTableEdit()
    {
        TableEdit = null;
        RemoveSyntheticPartitionsSection();
        UpdateSectionEditCounts();
    }

    partial void OnTableEditChanged(TableEditSession? oldValue, TableEditSession? newValue)
    {
        _virtualPreviewCts?.Cancel();
        _virtualPreviewCts = null;
        VirtualPreviewText = string.Empty; // 지난 편집의 미리보기를 새 편집에 남기지 않는다
        TableEditNeedsRefresh = false;
        if (oldValue is not null)
        {
            oldValue.Recomputed -= OnTableEditRecomputed;
            oldValue.VirtualExpressionEdited -= OnVirtualExpressionEdited;
        }

        if (newValue is not null)
        {
            newValue.Recomputed += OnTableEditRecomputed;
            newValue.VirtualExpressionEdited += OnVirtualExpressionEdited;
        }

        UpdateSectionEditCounts();
        RaiseTableEditProperties();
        OnTableEditStateChanged();
    }

    private void OnTableEditRecomputed(object? sender, EventArgs e)
    {
        UpdateSectionEditCounts();
        OnPropertyChanged(nameof(CanSaveTableEditScript));
        OnPropertyChanged(nameof(CanAddRow));
        OnPropertyChanged(nameof(CanAddToBundleNow));
        OnTableEditStateChanged();
    }

    /// <summary>편집 모드·페이지·문장이 바뀌면 도구줄·아래 줄 속성을 다시 알린다.</summary>
    private void RaiseTableEditProperties()
    {
        if (TableEdit is { } edit)
        {
            edit.CurrentPage = EditPage;
        }

        OnPropertyChanged(nameof(EditPage));
        OnPropertyChanged(nameof(CanToggleTableEdit));
        OnPropertyChanged(nameof(IsEditPageShown));
        OnPropertyChanged(nameof(ShowsTableToolbar));
        OnPropertyChanged(nameof(ShowsSectionTable));
        OnPropertyChanged(nameof(IsEditingColumns));
        OnPropertyChanged(nameof(IsEditingConstraints));
        OnPropertyChanged(nameof(IsEditingDescription));
        OnPropertyChanged(nameof(IsEditingPartitions));
        OnPropertyChanged(nameof(IsEditingStorage));
        OnPropertyChanged(nameof(ShowsRowToolbar));
        OnPropertyChanged(nameof(CanAddRow));
        OnPropertyChanged(nameof(CanSaveTableEditScript));
        OnPropertyChanged(nameof(CanRunTableEdit));
        OnPropertyChanged(nameof(RunIsDefault));
        OnPropertyChanged(nameof(CanAddToBundle));
        OnPropertyChanged(nameof(CanAddToBundleNow));
        OnPropertyChanged(nameof(IsDevelopmentConnection));
        OnPropertyChanged(nameof(IsProductionConnection));
        OnPropertyChanged(nameof(EnvironmentText));
    }

    /// <summary>편집 상태가 바뀌면 다시 알릴 속성(실행 쪽 partial 이 더한다).</summary>
    partial void OnTableEditStateChanged();

    /// <summary>페이지 이름 옆 바뀐 건수(설계 7.1).</summary>
    private void UpdateSectionEditCounts()
    {
        foreach (var s in Sections)
        {
            var page = s.Title switch
            {
                "열" => TableEditPage.Columns,
                "제약" => TableEditPage.Constraints,
                "인덱스" => TableEditPage.Indexes,
                "설명" => TableEditPage.Description,
                PartitionsTitle => TableEditPage.Partitions,
                StorageTitle => TableEditPage.Storage,
                _ => (TableEditPage?)null,
            };
            s.EditCount = TableEdit is { } edit && page is { } p ? edit.CountFor(p) : 0;
        }
    }

    private readonly List<ObjectDetailSection> _syntheticSections = [];

    /// <summary>
    /// 편집 중에만 있는 구역을 끼운다: 「파티션」(일반 테이블에는 없음 — 일반 → 파티션 길, 설계 18.4)은 「인덱스」 뒤, 「저장」(공급자가 만들지 않음, 설계 18.2)은 그 뒤.
    /// </summary>
    private void EnsurePartitionsSection()
    {
        if (!Sections.Any(s => s.Title == PartitionsTitle))
        {
            Insert(new ObjectDetailSection(new DetailSection(PartitionsTitle, ["항목", "값"],
                [[TableEditText.T("파티션", "Partitioning"), TableEditText.T("파티션 테이블이 아님", "not partitioned")]])), "인덱스");
        }

        if (!Sections.Any(s => s.Title == StorageTitle))
        {
            Insert(new ObjectDetailSection(new DetailSection(StorageTitle, ["항목", "값"],
                [[TableEditText.T("저장 속성", "Storage"), TableEditText.T("편집 중에만 보입니다", "shown while editing")]])), PartitionsTitle);
        }

        void Insert(ObjectDetailSection section, string after)
        {
            var anchor = Sections.FirstOrDefault(s => s.Title == after) ?? Sections.FirstOrDefault(s => s.Title == "인덱스");
            Sections.Insert(anchor is null ? Sections.Count : Sections.IndexOf(anchor) + 1, section);
            _syntheticSections.Add(section);
        }
    }

    private void RemoveSyntheticPartitionsSection()
    {
        foreach (var s in _syntheticSections)
        {
            if (ReferenceEquals(SelectedSection, s))
            {
                SelectedSection = Sections.FirstOrDefault(x => x.Title == "열");
            }

            Sections.Remove(s);
        }

        _syntheticSections.Clear();
    }

    /// <summary>캐시를 무효화하고 테이블을 새로 읽는다(TE-4·TE-28).</summary>
    private async Task<TableInfo> ReadTableFreshAsync(IMetadataProvider meta, ISession session, DbObjectRef target, CancellationToken ct)
    {
        if (InvalidateTableCache is { } invalidate)
        {
            await invalidate(session, target, ct);
        }
        else if (_services?.GetService(typeof(IMetadataCache)) is IMetadataCache cache)
        {
            await cache.InvalidateAsync(session.DbKey, [target], ct);
        }

        return await meta.GetTableAsync(session, target, ct);
    }
}
