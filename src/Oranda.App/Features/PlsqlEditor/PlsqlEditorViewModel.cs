using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>컴파일 시도 결과.</summary>
public enum PlsqlCompileStatus
{
    /// <summary>오류 없이 컴파일됨.</summary>
    Compiled,

    /// <summary>컴파일됐지만 서버가 오류를 알렸다(객체는 INVALID).</summary>
    CompiledWithErrors,

    /// <summary>편집을 시작한 뒤 서버 소스가 바뀌었다. 보내지 않았다(<c>force</c> 로 덮어쓸 수 있다).</summary>
    Conflict,

    /// <summary>다른 세션이 객체를 쓰고 있다. 보내지 않았다(<c>force</c> 로 진행할 수 있다).</summary>
    InUse,

    /// <summary>서버가 문장을 거부했다(ORA 오류).</summary>
    Failed,

    /// <summary>정책 관문이 막았거나 사용자가 확인을 거절했다.</summary>
    Blocked,

    NoSession,

    NothingToCompile,
}

public sealed record PlsqlCompileOutcome(PlsqlCompileStatus Status, IReadOnlyList<CompileError> Errors, string Message);

/// <summary>
/// <c>oranda.panel.plsqlEditor</c> — PL/SQL 소스 편집 탭(TSK-08-01). 안쪽에 SQL 편집기 문서(<see cref="Editor"/>)를 품는다.
/// 소스는 <see cref="IMetadataProvider"/> 로 읽고, 컴파일(<c>CREATE OR REPLACE</c>)은 반드시 <see cref="IExecutionService"/> 로 보낸다(정책 관문).
/// 충돌 확인: 열 때의 <c>last_ddl_time</c> 을 저장 직전 값과 비교한다. 서버에 잠금 테이블을 만들지 않는다(C-05).
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.plsqlEditor", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class PlsqlEditorViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private IMetadataProvider? _meta;
    private IExecutionService? _exec;
    private IEventBus? _events;
    private ISession? _session;
    private DateTime? _baseline;
    private int _generation;
    private int _line1Shift;

    public PlsqlEditorViewModel()
    {
        Editor = new SqlEditorViewModel();
    }

    public PlsqlEditorViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 제공자·실행 서비스를 직접 넣는다.</summary>
    internal PlsqlEditorViewModel(IMetadataProvider meta, IExecutionService exec, IEventBus? events = null)
        : this()
    {
        _meta = meta;
        _exec = exec;
        _events = events;
    }

    /// <summary>지금 포커스를 가진 PL/SQL 편집 탭(명령 처리기가 쓴다). 보기가 설정한다.</summary>
    public static PlsqlEditorViewModel? Active { get; set; }

    /// <summary>글자 편집 쪽(구문 강조·찾기·실행 취소는 SQL 편집기 것을 그대로 쓴다).</summary>
    public SqlEditorViewModel Editor { get; }

    public DbObjectRef? Target { get; private set; }

    public ISession? Session => _session;

    /// <summary>오류·경고(서버 순서).</summary>
    public ObservableCollection<CompileError> Errors { get; } = [];

    /// <summary>서버에서 마지막으로 읽은 소스(차이 보기 기준). 없으면 null.</summary>
    public string? ServerSource { get; private set; }

    /// <summary>열 때(또는 마지막 컴파일 때) 읽은 <c>last_ddl_time</c>.</summary>
    public DateTime? BaselineDdlTime => _baseline;

    [ObservableProperty]
    public partial string Title { get; set; } = "PL/SQL";

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>편집을 시작한 뒤 서버 소스가 바뀌었다(문맥 키 <c>serverSourceChanged</c>).</summary>
    [ObservableProperty]
    public partial bool ServerSourceChanged { get; set; }

    /// <summary>차이 보기 글자(<c>-</c> 내 쪽만, <c>+</c> 서버만). 닫혀 있으면 빈 문자열.</summary>
    [ObservableProperty]
    public partial string DiffText { get; set; } = string.Empty;

    /// <summary>다른 세션 사용 경고 등 저장 전 확인이 필요한 말.</summary>
    [ObservableProperty]
    public partial string WarningText { get; set; } = string.Empty;

    /// <summary>명세/본문 쌍이 있는 유형(문맥 키 <c>isPackageDoc</c>).</summary>
    public bool IsPackageDoc => Target is { } t && PlsqlSourceText.SiblingType(t.ObjectType) is not null;

    public bool HasConflictBanner => ServerSourceChanged || !string.IsNullOrEmpty(WarningText);

    partial void OnTitleChanged(string value) => Editor.Title = value;

    partial void OnServerSourceChangedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasConflictBanner));
        SyncKeys();
    }

    partial void OnWarningTextChanged(string value) => OnPropertyChanged(nameof(HasConflictBanner));

    /// <summary>새 PL/SQL 문서(아직 서버 객체가 아님). 글자만 채운다.</summary>
    public void StartNew(string text, ISession? session)
    {
        _session = session;
        Target = null;
        _baseline = null;
        _line1Shift = 0;
        ServerSource = null;
        Title = "새 PL/SQL";
        Editor.LoadText(text);
        Editor.ConnectionProfileId = session?.Profile.Id;
        StatusText = "새 문서 — 컴파일하면 서버에 만든다";
        SyncKeys();
    }

    /// <summary>서버에서 소스를 읽어 편집기에 채운다. 못 읽으면 예외 대신 상태 줄에 말하고 false.</summary>
    public async Task<bool> OpenAsync(ISession session, DbObjectRef target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);

        _session = session;
        Target = target;
        Title = $"{target.Owner}.{target.Name}" + (target.ObjectType.EndsWith("BODY", StringComparison.Ordinal) ? " (본문)" : string.Empty);
        var gen = Interlocked.Increment(ref _generation);
        var meta = ResolveMeta();
        if (meta is null)
        {
            StatusText = "메타데이터 제공자가 없다";
            return false;
        }

        IsBusy = true;
        try
        {
            var ddl = await ReadLastDdlAsync(meta, session, target, ct).ConfigureAwait(true);
            var source = await meta.GetSourceAsync(session, target, ct).ConfigureAwait(true);
            if (gen != _generation)
            {
                return false;
            }

            _baseline = ddl;
            ServerSource = source;
            Editor.LoadText(PlsqlSourceText.ToEditorText(source, target.Owner, out var shift));
            _line1Shift = shift;
            Editor.ConnectionProfileId = session.Profile.Id;
            ServerSourceChanged = false;
            DiffText = string.Empty;
            WarningText = string.Empty;
            StatusText = $"{target.ObjectType} {target.Owner}.{target.Name}";
            await LoadErrorsAsync(meta, session, target, ct).ConfigureAwait(true);
            OnPropertyChanged(nameof(IsPackageDoc));
            SyncKeys();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = "소스를 읽지 못했다: " + ex.Message;
            return false;
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>서버 소스가 열 때와 달라졌는지 지금 확인한다. 달라졌으면 차이 보기를 채운다.</summary>
    public async Task<bool> CheckServerChangedAsync(CancellationToken ct = default)
    {
        if (Target is null || _session is null || ResolveMeta() is not { } meta)
        {
            return false;
        }

        var now = await ReadLastDdlAsync(meta, _session, Target, ct).ConfigureAwait(true);
        var changed = _baseline is not null && now is not null && now != _baseline;
        if (changed)
        {
            ServerSource = await meta.GetSourceAsync(_session, Target, ct).ConfigureAwait(true);
            DiffText = PlsqlSourceText.BuildDiff(Editor.Text, ServerSource);
        }

        ServerSourceChanged = changed;
        return changed;
    }

    /// <summary>저장(컴파일). <paramref name="force"/> = 충돌·다른 세션 사용 경고를 알고도 진행.</summary>
    public async Task<PlsqlCompileOutcome> CompileAsync(bool force = false, CancellationToken ct = default)
    {
        _session ??= ResolveSession();
        if (_session is null)
        {
            return Report(PlsqlCompileStatus.NoSession, "접속이 없다");
        }

        var text = PlsqlSourceText.ToCompileText(Editor.Text);
        var header = PlsqlSourceText.ParseHeader(text);
        if (string.IsNullOrWhiteSpace(text) || header is null)
        {
            return Report(PlsqlCompileStatus.NothingToCompile, "PACKAGE·PROCEDURE·FUNCTION·TRIGGER·TYPE 소스가 아니다");
        }

        var owner = header.Value.Owner ?? Target?.Owner ?? _session.Profile.User.ToUpperInvariant();
        var effective = new DbObjectRef(owner, header.Value.Name, header.Value.ObjectType);
        if (Target is null || Target.Owner != effective.Owner || Target.Name != effective.Name || Target.ObjectType != effective.ObjectType)
        {
            // 이름·유형이 바뀌었거나 새 문서: 새 객체로 본다(충돌 기준 없음).
            Target = effective;
            _baseline = null;
            ServerSource = null;
            Title = $"{effective.Owner}.{effective.Name}";
            OnPropertyChanged(nameof(IsPackageDoc));
        }

        var meta = ResolveMeta();
        var exec = ResolveExec();
        if (exec is null)
        {
            return Report(PlsqlCompileStatus.NoSession, "실행 서비스가 없다");
        }

        IsBusy = true;
        try
        {
            if (!force && meta is not null && _baseline is not null && await CheckServerChangedAsync(ct).ConfigureAwait(true))
            {
                return Report(PlsqlCompileStatus.Conflict, "다른 사람이 서버 소스를 바꿨다 — 차이를 확인하고 덮어쓸지 정한다");
            }

            if (!force)
            {
                var others = await CountOtherUsersAsync(exec, _session, effective, ct).ConfigureAwait(true);
                if (others > 0)
                {
                    WarningText = $"다른 세션 {others}개가 이 객체를 쓰고 있다. 지금 컴파일하면 기다리거나 그 세션이 영향을 받는다.";
                    return Report(PlsqlCompileStatus.InUse, WarningText);
                }
            }

            WarningText = string.Empty;
            DiffText = string.Empty;
            await ArchiveServerSourceAsync(meta, effective, ct).ConfigureAwait(true);
            var stmt = new SqlStatement(text, default, StatementKind.Ddl) { Verb = "CREATE" };
            var result = await exec.ExecuteAsync(_session, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Editor }, ct).ConfigureAwait(true);
            if (result.ResultSet is not null)
            {
                await result.ResultSet.DisposeAsync().ConfigureAwait(true);
            }

            switch (result.Status)
            {
                case ExecutionStatus.Denied:
                    return Report(PlsqlCompileStatus.Blocked, "정책이 컴파일을 막았다");
                case ExecutionStatus.Cancelled:
                    return Report(PlsqlCompileStatus.Blocked, "컴파일이 취소됐다");
                case ExecutionStatus.Failed:
                    return Report(PlsqlCompileStatus.Failed, result.Error is { } e ? $"{e.Code} {e.Message}" : "컴파일 실패");
            }

            IReadOnlyList<CompileError> errors = [];
            if (meta is not null)
            {
                errors = await LoadErrorsAsync(meta, _session, effective, ct).ConfigureAwait(true);
                _baseline = await ReadLastDdlAsync(meta, _session, effective, ct).ConfigureAwait(true);
            }

            ServerSource = PlsqlSourceText.ToEditorText(text);
            ServerSourceChanged = false;
            Editor.MarkSaved();
            var errorCount = errors.Count(x => !x.IsWarning);
            return Report(errorCount > 0 ? PlsqlCompileStatus.CompiledWithErrors : PlsqlCompileStatus.Compiled,
                errorCount > 0 ? $"컴파일됐지만 오류 {errorCount}건" : errors.Count > 0 ? $"컴파일됨 (경고 {errors.Count}건)" : "컴파일됨", errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Report(PlsqlCompileStatus.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>오류의 줄·열을 편집기 위치로 옮겨 커서를 둔다. 서버 오류는 소스 시작(유형 낱말)부터 센다.</summary>
    public bool NavigateTo(CompileError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (lineOffset, colOffset) = PlsqlSourceText.SourceStart(Editor.Text);
        var line = error.Line + lineOffset;
        if (line < 1 || line > Editor.Document.LineCount)
        {
            return false;
        }

        var dl = Editor.Document.GetLineByNumber(line);
        var col = error.Column + (error.Line == 1 ? colOffset + _line1Shift : 0);
        var offset = dl.Offset + Math.Clamp(col - 1, 0, dl.Length);
        Editor.Select(offset, 0);
        Editor.Surface?.ScrollToLine(line);
        Editor.Surface?.FocusEditor();
        return true;
    }

    /// <summary>
    /// 서버 소스 줄 번호(<c>all_source.line</c>, 1부터)로 커서를 옮긴다. 앞에 붙인 <c>CREATE OR REPLACE</c>·머리 주석만큼 줄을 맞춘다.
    /// 편집기가 아직 화면에 붙지 않았으면 붙을 때 그 줄을 보이도록 <c>ScrollLine</c> 도 정한다. 줄이 범위 밖이면 false.
    /// </summary>
    public bool GoToServerLine(int line)
    {
        var (lineOffset, _) = PlsqlSourceText.SourceStart(Editor.Text);
        var target = line + lineOffset;
        if (line < 1 || target > Editor.Document.LineCount)
        {
            return false;
        }

        Editor.ScrollLine = target;
        return Editor.GoToLine(target);
    }

    /// <summary>차이 보기 열기(서버 소스를 새로 읽어 내 소스와 비교).</summary>
    public async Task ShowServerDiffAsync(CancellationToken ct = default)
    {
        if (Target is null || _session is null || ResolveMeta() is not { } meta)
        {
            return;
        }

        try
        {
            ServerSource = await meta.GetSourceAsync(_session, Target, ct).ConfigureAwait(true);
            var diff = PlsqlSourceText.BuildDiff(Editor.Text, ServerSource);
            DiffText = diff.Length == 0 ? "(차이 없음)" : diff;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = "서버 소스를 읽지 못했다: " + ex.Message;
        }
    }

    public void CloseDiff() => DiffText = string.Empty;

    /// <summary>
    /// 탭을 닫을 때 저장 여부를 물어야 하는가(TSK-13-27, SQL 탭 규칙과 같다). 읽기 전용은 묻지 않는다.
    /// 파일에 묶인 탭 = 파일과 달라졌을 때, 서버 객체를 열었거나 컴파일한 탭 = 마지막 읽기·컴파일 뒤로 고쳤을 때, 그 밖의 새 탭 = 내용이 있을 때.
    /// </summary>
    public bool NeedsSaveConfirm
    {
        get
        {
            if (Editor.IsReadOnly)
            {
                return false;
            }

            if (Editor.FilePath is null && Target is null)
            {
                return !string.IsNullOrWhiteSpace(Editor.Text);
            }

            return Editor.IsDirty;
        }
    }

    /// <summary>
    /// 닫기 확인: 저장 / 저장 안 함 / 취소를 묻고 닫아도 되면 true. 저장은 로컬 파일 저장이다(파일에 묶였으면 그 파일, 아니면 다른 이름으로).
    /// 서버에는 컴파일하지 않는다: 서버 반영은 명시적 컴파일로만 한다.
    /// </summary>
    public Task<bool> ConfirmCloseAsync(SqlEditorHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        return NeedsSaveConfirm ? host.AskAndSaveAsync(Editor, Title, ct) : Task.FromResult(true);
    }

    /// <summary>시험용: 문서 탭을 여는 방법(기본 = 셸 도킹).</summary>
    internal Func<PlsqlEditorViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>
    /// 명세 ↔ 본문 전환: 반대쪽 소스를 새 탭으로 연다(편집 중인 글을 잃지 않게 이 탭은 그대로 둔다).
    /// 반대쪽이 서버에 없으면 탭을 만들지 않고 상태 줄에 말한다.
    /// </summary>
    public async Task<PlsqlEditorViewModel?> OpenSiblingAsync(CancellationToken ct = default)
    {
        if (Target is not { } t || _session is null || PlsqlSourceText.SiblingType(t.ObjectType) is not { } siblingType)
        {
            return null;
        }

        var vm = _services is not null ? new PlsqlEditorViewModel(_services) : new PlsqlEditorViewModel(_meta!, _exec!);
        vm.OpenDocument = OpenDocument;
        if (!await vm.OpenAsync(_session, t with { ObjectType = siblingType }, ct).ConfigureAwait(true))
        {
            StatusText = $"{siblingType} 소스가 없다: " + vm.StatusText;
            return null;
        }

        return PlsqlEditorCommands.Dock(_services, OpenDocument, vm) ? vm : null;
    }

    private PlsqlCompileOutcome Report(PlsqlCompileStatus status, string message, IReadOnlyList<CompileError>? errors = null)
    {
        StatusText = message;
        return new PlsqlCompileOutcome(status, errors ?? [], message);
    }

    private async Task<IReadOnlyList<CompileError>> LoadErrorsAsync(IMetadataProvider meta, ISession session, DbObjectRef target, CancellationToken ct)
    {
        IReadOnlyList<CompileError> errors;
        try
        {
            errors = await meta.GetCompileErrorsAsync(session, target, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            errors = [];
        }

        Errors.Clear();
        foreach (var e in errors)
        {
            Errors.Add(e);
        }

        CompileErrorsViewModel.Publish(this);
        return errors;
    }

    private static async Task<DateTime?> ReadLastDdlAsync(IMetadataProvider meta, ISession session, DbObjectRef target, CancellationToken ct)
    {
        var detail = await meta.GetObjectDetailAsync(session, target, ct).ConfigureAwait(true);
        var row = detail.Sections.FirstOrDefault(s => s.Title == "일반")?.Rows.FirstOrDefault(r => r.Count > 1 && Equals(r[0], "마지막 DDL"));
        return row?[1] switch
        {
            DateTime d => d,
            DateTimeOffset o => o.UtcDateTime,
            _ => null,
        };
    }

    /// <summary>이 객체를 쓰고 있는 다른 세션 수(v$access). 권한이 없거나 못 읽으면 0(경고만 못 한다).</summary>
    private static async Task<int> CountOtherUsersAsync(IExecutionService exec, ISession session, DbObjectRef target, CancellationToken ct)
    {
        static string Lit(string s) => s.Replace("'", "''", StringComparison.Ordinal);

        var sql = $"SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = '{Lit(target.Owner)}' AND object = '{Lit(target.Name)}' "
            + "AND sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))";
        try
        {
            var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" };
            var r = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct).ConfigureAwait(true);
            if (r.ResultSet is null)
            {
                return 0;
            }

            await using (r.ResultSet.ConfigureAwait(true))
            {
                var page = await r.ResultSet.GetPageAsync(0, ct).ConfigureAwait(true);
                return page.RowCount > 0 && page.Columns.Count > 0 ? Convert.ToInt32(page.Columns[0][0], CultureInfo.InvariantCulture) : 0;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private void SyncKeys()
    {
        if (_services?.GetService(typeof(ContextKeyService)) is not ContextKeyService keys)
        {
            return;
        }

        keys.Set("isPackageDoc", IsPackageDoc);
        keys.Set("serverSourceChanged", ServerSourceChanged);
    }

    /// <summary>
    /// 변경 일지(TSK-13-07): 컴파일이 서버 소스를 덮어쓰기 직전에 지금 서버에 있는 소스를 보관한다(같은 내용은 기록기가 쌓지 않는다).
    /// 객체가 아직 없으면(새 객체) 보관할 것이 없어 건너뛴다. 읽기 실패는 일지에 이유만 남기고 컴파일은 계속한다.
    /// 문장에 <c>ChangeTarget</c> 을 두지 않으므로 실행 엔진의 DDL 보관과 겹치지 않는다.
    /// </summary>
    private async Task ArchiveServerSourceAsync(IMetadataProvider? meta, DbObjectRef target, CancellationToken ct)
    {
        if (meta is null || _session is null || ResolveEvents() is not { } events)
        {
            return;
        }

        var entry = new JournalEntry
        {
            CapturedAt = DateTimeOffset.UtcNow,
            ProfileId = _session.Profile.Id,
            ProfileName = _session.Profile.Name,
            DbKey = _session.DbKey,
            ServerName = _session.Profile.DataSource,
            Owner = target.Owner,
            Name = target.Name,
            ObjectType = target.ObjectType,
            Cause = JournalCause.Compile,
            StatementSummary = $"컴파일 {target.ObjectType} {target.Owner}.{target.Name}",
        };
        try
        {
            // 보관이 컴파일을 오래 붙잡지 않게 제한 시간을 둔다(사용자 취소는 그대로 전파).
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            string source;
            try
            {
                source = await meta.GetSourceAsync(_session, target, limit.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("10초 안에 읽지 못했다");
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                // 되돌리기로 새 편집기에 열면 바로 컴파일되는 글자(CREATE OR REPLACE + 소유자)로 보관한다.
                events.Publish(new ChangeSnapshotCaptured(entry with { PreviousDdl = PlsqlSourceText.ToEditorText(source, target.Owner, out _) }));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // 소스를 찾을 수 없다: 새 객체라 보관할 이전 소스가 없다.
        }
        catch (Exception ex)
        {
            events.Publish(new ChangeSnapshotCaptured(entry with { FailureReason = "서버 소스를 읽지 못했다: " + ex.Message }));
        }
    }

    private IEventBus? ResolveEvents() => _events ??= (IEventBus?)_services?.GetService(typeof(IEventBus));

    private IMetadataProvider? ResolveMeta() => _meta ??= (IMetadataProvider?)_services?.GetService(typeof(IMetadataProvider));

    private IExecutionService? ResolveExec() => _exec ??= (IExecutionService?)_services?.GetService(typeof(IExecutionService));

    private ISession? ResolveSession()
        => ConnectionManager.SessionRouter.CurrentOf(_services);
}
