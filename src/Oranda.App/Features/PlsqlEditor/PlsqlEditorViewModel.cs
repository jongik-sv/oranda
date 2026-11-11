using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.PlsqlWorkspace;
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
/// 패키지·타입은 탭 하나에 명세와 본문 두 쪽(<see cref="PlsqlSourcePart"/>)을 갖고 「명세 | 본문」으로 바꿔 본다(PKG1, D-067). 쪽마다 글·되돌리기·고침 표시가 따로다.
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.plsqlEditor", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class PlsqlEditorViewModel : ToolPanelViewModelBase, ICompileErrorSource, IPlsqlRunSource, ConnectionManager.IConnectionDocument
{
    private readonly IServiceProvider? _services;
    private IMetadataProvider? _meta;
    private IExecutionService? _exec;
    private IEventBus? _events;
    private ISession? _session;
    private int _generation;

    /// <summary>처음 만든 쪽(새 문서·프로시저는 이 쪽 하나). 실행 표시(<see cref="Run"/>)는 늘 이 쪽 편집기 것이다.</summary>
    private readonly PlsqlSourcePart _primary;

    /// <summary>명세/본문 쌍의 다른 쪽. 쌍이 없는 유형이면 null.</summary>
    private PlsqlSourcePart? _secondary;

    /// <summary>보이는 쪽.</summary>
    private PlsqlSourcePart _current;

    /// <summary>복원한 탭이 연 접속 프로필(결정 71). 그 프로필의 세션이 붙기 전에는 서버 일을 하지 않고, 다른 접속으로 컴파일하지 않는다.</summary>
    private Guid? _restoredProfileId;

    public PlsqlEditorViewModel()
    {
        _primary = NewPart(PlsqlSide.Spec, new SqlEditorViewModel());
        _current = _primary;
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

    /// <summary>
    /// 지금 포커스를 가진 PL/SQL 편집 탭(명령 처리기가 쓴다). 보기와 작업 공간 라우터가 설정하며, 바뀌면 그 탭의 문맥 키를 맞춘다.
    /// null 이 되면(탭을 닫음, PL/SQL 이 아닌 문서로 옮김) <c>isPackageDoc</c>·<c>serverSourceChanged</c>·<c>plsqlRunnable</c>·<c>plsqlHasSelection</c>·<c>plsqlCanUndo</c>·<c>plsqlCanRedo</c> 를 끈다.
    /// </summary>
    public static PlsqlEditorViewModel? Active
    {
        get;
        set
        {
            var old = field;
            field = value;
            if (value is not null)
            {
                value.SyncKeys();
            }
            else
            {
                ClearKeys(old?.Keys ?? ContextKeyService.Shared);
            }
        }
    }

    /// <summary>보이는 쪽의 글자 편집기(구문 강조·찾기·실행 취소는 SQL 편집기 것을 그대로 쓴다). 쪽을 바꾸면 다른 편집기가 된다.</summary>
    public SqlEditorViewModel Editor => _current.Editor;

    /// <summary>이 탭에서 연 PL/SQL 실행 창의 실행 표시(쪽을 바꿔도 같은 것).</summary>
    public RunMarker Run => _primary.Editor.Run;

    /// <summary>보이는 쪽의 서버 객체. 새 문서면 null.</summary>
    public DbObjectRef? Target => _current.Target;

    /// <summary>편집기에서 실행(<c>oranda.plsql.run</c>)할 대상: 서버에서 연 프로시저·함수·패키지. 새 문서·실행 못 하는 유형이면 null.</summary>
    public DbObjectRef? RunTarget => !AwaitingSession && Target is { } t && PlsqlRunCommands.IsRunnableType(t.ObjectType) ? t : null;

    /// <summary>복원한 탭이 제 접속을 기다린다(실행·명세/본문 전환 키가 꺼진다).</summary>
    public bool AwaitingSession => _restoredProfileId is not null && _session is null;

    /// <summary>복원한 탭이 기다리는 접속 프로필(복원한 탭이 아니면 null).</summary>
    public Guid? RestoredProfileId => _restoredProfileId;

    /// <summary>패키지 멤버는 대화상자에서 고른다.</summary>
    public string? RunMember => null;

    public ISession? Session
    {
        get => _session;
        private set
        {
            if (!ReferenceEquals(_session, value))
            {
                _session = value;
                OnPropertyChanged(nameof(Session));
                OnPropertyChanged(nameof(ConnectionName));
                OnPropertyChanged(nameof(AwaitingSession));
                OnPropertyChanged(nameof(IsPackageDoc));
                SyncKeys(); // 복원 탭이 접속을 기다리다 세션을 얻으면(붙이기·컴파일) 실행·명세/본문 키가 켜진다
                SessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary><see cref="Session"/> 이 바뀌었다(툴바 접속 콤보가 따른다).</summary>
    public event EventHandler? SessionChanged;

    /// <summary>컴파일 중이면 툴바 접속 콤보로 접속을 바꾸지 않는다.</summary>
    public bool IsSessionBusy => IsBusy;

    /// <summary>
    /// 툴바 접속 콤보로 이 탭의 접속을 바꾼다(TSK-12-04): 다음 컴파일·실행부터 <paramref name="session"/> 의 DB 로 간다.
    /// 컴파일 중이면 바꾸지 않는다(false). 복원한 탭이 기다리던 접속은 잊고, 이전 접속에서 읽은 서버 기준(마지막 DDL 시각·서버 소스·차이)은 두 쪽 다 버린다.
    /// </summary>
    public bool UseSession(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ReferenceEquals(_session, session))
        {
            return true;
        }

        if (IsBusy)
        {
            return false;
        }

        Interlocked.Increment(ref _generation); // 이전 접속에서 읽던 소스가 뒤늦게 덮지 않게
        _restoredProfileId = null;
        foreach (var part in Parts)
        {
            part.Baseline = null;
            part.ServerSource = null;
            SetServerChanged(part, false);
            part.Editor.ConnectionProfileId = session.Profile.Id; // 자동 완성도 같은 접속을 본다
        }

        DiffText = string.Empty;
        Session = session;
        return true;
    }

    /// <summary>탭 머리에 보일 접속 이름(작업 공간 안에 접속이 섞였을 때). 세션이 없으면 null.</summary>
    public string? ConnectionName => _session?.Profile.Name;

    /// <summary>PL/SQL 작업 공간의 미리보기 탭(머리글 기울임). 고치거나 고정하면 false.</summary>
    [ObservableProperty]
    public partial bool IsPreviewTab { get; set; }

    /// <summary>오류·경고(서버 순서). 패키지·타입은 명세 쪽 다음 본문 쪽이고, 줄마다 <see cref="CompileError.ObjectType"/> 으로 쪽을 가른다.</summary>
    public ObservableCollection<CompileError> Errors { get; } = [];

    /// <summary>보이는 쪽의 서버에서 마지막으로 읽은 소스(차이 보기 기준). 없으면 null.</summary>
    public string? ServerSource => _current.ServerSource;

    /// <summary>보이는 쪽을 열 때(또는 마지막 컴파일 때) 읽은 <c>last_ddl_time</c>.</summary>
    public DateTime? BaselineDdlTime => _current.Baseline;

    [ObservableProperty]
    public partial string Title { get; set; } = "PL/SQL";

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>편집을 시작한 뒤 보이는 쪽의 서버 소스가 바뀌었다(문맥 키 <c>serverSourceChanged</c>).</summary>
    public bool ServerSourceChanged
    {
        get => _current.ServerChanged;
        set => SetServerChanged(_current, value);
    }

    /// <summary>차이 보기 글자(<c>-</c> 내 쪽만, <c>+</c> 서버만). 닫혀 있으면 빈 문자열.</summary>
    [ObservableProperty]
    public partial string DiffText { get; set; } = string.Empty;

    /// <summary>나란히 보기 행(왼쪽 = 서버, 오른쪽 = 내 소스). 차이가 없거나 닫혀 있으면 빈 목록.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MapperDiffRow> DiffRows { get; set; } = [];

    partial void OnDiffTextChanged(string value)
    {
        if (value.Length == 0)
        {
            DiffRows = [];
        }
    }

    /// <summary>내 소스와 서버 소스의 차이를 글자·나란히 보기 행으로 채운다. 차이가 없으면 둘 다 비운다.</summary>
    private void FillDiff(PlsqlSourcePart part, string server)
    {
        DiffText = PlsqlSourceText.BuildDiff(part.Editor.Text, server);
        DiffRows = DiffText.Length == 0
            ? []
            : MapperDiffRow.Section(null, MapperLineDiff.Build(PlsqlSourceText.ForCompare(server), PlsqlSourceText.ForCompare(part.Editor.Text), 1, 1), sideBySide: true);
    }

    /// <summary>다른 세션 사용 경고 등 저장 전 확인이 필요한 말.</summary>
    [ObservableProperty]
    public partial string WarningText { get; set; } = string.Empty;

    public bool HasConflictBanner => ServerSourceChanged || !string.IsNullOrEmpty(WarningText);

    partial void OnTitleChanged(string value)
    {
        foreach (var part in Parts)
        {
            part.Editor.Title = value;
        }
    }

    partial void OnWarningTextChanged(string value) => OnPropertyChanged(nameof(HasConflictBanner));

    private void SetServerChanged(PlsqlSourcePart part, bool value)
    {
        if (part.ServerChanged == value)
        {
            return;
        }

        part.ServerChanged = value;
        if (ReferenceEquals(part, _current))
        {
            OnPropertyChanged(nameof(ServerSourceChanged));
            OnPropertyChanged(nameof(HasConflictBanner));
            SyncKeys();
        }
    }

    /// <summary>새 PL/SQL 문서(아직 서버 객체가 아님). 글자만 채운다.</summary>
    public void StartNew(string text, ISession? session)
    {
        _restoredProfileId = null;
        Session = session;
        UseSinglePart(null);
        Title = "새 PL/SQL";
        Editor.LoadText(text);
        Editor.ConnectionProfileId = session?.Profile.Id;
        StatusText = "새 문서 — 컴파일하면 서버에 만든다";
        SyncKeys();
    }

    /// <summary>
    /// 디스크의 PL/SQL 파일(<c>.pkb</c> 등)을 연 탭. 글은 파일 그대로이고 편집기가 파일에 묶인다(Ctrl+S = 그 파일에 저장, 인코딩 유지).
    /// 서버 객체는 아니므로 대상 없음 — 컴파일 때 머리글로 정한다(새 문서와 같다).
    /// </summary>
    public void StartFile(string path, string text, string encoding, ISession? session)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _restoredProfileId = null;
        Session = session;
        UseSinglePart(null);
        Title = System.IO.Path.GetFileName(path);
        Editor.LoadText(text);
        Editor.FilePath = path;
        Editor.Encoding = encoding;
        Editor.ConnectionProfileId = session?.Profile.Id;
        StatusText = "파일 — 컴파일하면 서버에 만든다";
        SyncKeys();
    }

    /// <summary>
    /// 서버에서 소스를 읽어 편집기에 채운다. 못 읽으면 예외 대신 상태 줄에 말하고 false.
    /// 패키지·타입은 <paramref name="target"/> 쪽(명세 또는 본문)을 보이고, 다른 쪽도 함께 읽는다. 다른 쪽이 서버에 없으면 빈 쪽(「본문 만들기」 안내)으로 둔다.
    /// </summary>
    public async Task<bool> OpenAsync(ISession session, DbObjectRef target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);

        _restoredProfileId = null;
        Session = session;
        var (want, other) = Arrange(target);
        Title = TitleOf(target);
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
            var ddl = await ReadLastDdlAsync(meta, session, want.Target!, ct).ConfigureAwait(true);
            var source = await meta.GetSourceAsync(session, want.Target!, ct).ConfigureAwait(true);
            if (gen != _generation)
            {
                return false;
            }

            Fill(want, source, ddl, session);
            want.Errors = await ReadErrorsAsync(meta, session, want, ct).ConfigureAwait(true);
            if (other is not null && gen == _generation)
            {
                await LoadCompanionAsync(meta, session, other, gen, ct).ConfigureAwait(true);
            }

            if (gen != _generation)
            {
                return false;
            }

            DiffText = string.Empty;
            WarningText = string.Empty;
            StatusText = $"{target.ObjectType} {target.Owner}.{target.Name}";
            PublishErrors(publish: false);
            RefreshMembers();
            RaiseSides();
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

    /// <summary>「서버 소스 다시 읽기」: 보이는 쪽만 서버에서 다시 읽는다(다른 쪽의 고친 글은 그대로).</summary>
    public async Task<bool> ReloadAsync(CancellationToken ct = default)
    {
        var part = _current;
        if (part.Target is not { } target || _session is not { } session || ResolveMeta() is not { } meta)
        {
            return false;
        }

        var gen = Interlocked.Increment(ref _generation);
        IsBusy = true;
        try
        {
            var ddl = await ReadLastDdlAsync(meta, session, target, ct).ConfigureAwait(true);
            var source = await meta.GetSourceAsync(session, target, ct).ConfigureAwait(true);
            if (gen != _generation)
            {
                return false;
            }

            Fill(part, source, ddl, session);
            part.Errors = await ReadErrorsAsync(meta, session, part, ct).ConfigureAwait(true);
            DiffText = string.Empty;
            WarningText = string.Empty;
            StatusText = $"{target.ObjectType} {target.Owner}.{target.Name}";
            PublishErrors(publish: false);
            RefreshMembers();
            RaiseSides();
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

    /// <summary>서버에서 읽은 소스로 쪽을 채운다(서버 기준·첫 줄 보정·고침 없음).</summary>
    private void Fill(PlsqlSourcePart part, string source, DateTime? ddl, ISession session)
    {
        part.Baseline = ddl;
        part.ServerSource = source;
        part.Editor.LoadText(PlsqlSourceText.ToEditorText(source, part.Target?.Owner, out var shift));
        part.Line1Shift = shift;
        part.Editor.ConnectionProfileId = session.Profile.Id;
        part.Missing = false;
        part.Loaded = true;
        SetServerChanged(part, false);
    }

    /// <summary>서버 소스가 열 때와 달라졌는지 지금 확인한다(보이는 쪽). 달라졌으면 차이 보기를 채운다.</summary>
    public Task<bool> CheckServerChangedAsync(CancellationToken ct = default) => CheckServerChangedAsync(_current, ct);

    private async Task<bool> CheckServerChangedAsync(PlsqlSourcePart part, CancellationToken ct)
    {
        if (part.Target is null || _session is null || ResolveMeta() is not { } meta)
        {
            return false;
        }

        var now = await ReadLastDdlAsync(meta, _session, part.Target, ct).ConfigureAwait(true);
        var changed = part.Baseline is not null && now is not null && now != part.Baseline;
        if (changed)
        {
            part.ServerSource = await meta.GetSourceAsync(_session, part.Target, ct).ConfigureAwait(true);
            if (ReferenceEquals(part, _current))
            {
                FillDiff(part, part.ServerSource);
            }
        }

        SetServerChanged(part, changed);
        return changed;
    }

    /// <summary>저장(컴파일) — 보이는 쪽만(「이 쪽만 컴파일」). <paramref name="force"/> = 충돌·다른 세션 사용 경고를 알고도 진행.</summary>
    public Task<PlsqlCompileOutcome> CompileAsync(bool force = false, CancellationToken ct = default) => CompilePartAsync(_current, force, ct);

    private async Task<PlsqlCompileOutcome> CompilePartAsync(PlsqlSourcePart part, bool force, CancellationToken ct)
    {
        Session ??= ResolveSession();
        if (_session is null)
        {
            return Report(PlsqlCompileStatus.NoSession, _restoredProfileId is null ? "접속이 없다" : "이 탭을 연 접속이 아직 열리지 않았다 — 그 접속을 열면 컴파일한다");
        }

        var (text, hasMore) = PlsqlSourceText.SplitCompileUnit(part.Editor.Text);
        if (hasMore)
        {
            return Report(PlsqlCompileStatus.NothingToCompile, "단위가 여러 개인 파일은 컴파일하지 않는다 — SQL 탭에서 스크립트로 실행한다");
        }

        var header = PlsqlSourceText.ParseHeader(text);
        if (string.IsNullOrWhiteSpace(text) || header is null)
        {
            return Report(PlsqlCompileStatus.NothingToCompile, "PACKAGE·PROCEDURE·FUNCTION·TRIGGER·TYPE 소스가 아니다");
        }

        // 명세 쪽에는 명세, 본문 쪽에는 본문만(한 탭 두 쪽이 서로 덮어쓰지 않게).
        if (_secondary is not null && PlsqlSourceText.SiblingType(header.Value.ObjectType) is not null
            && PlsqlSourceText.IsBodyType(header.Value.ObjectType) != (part.Side == PlsqlSide.Body))
        {
            return Report(PlsqlCompileStatus.NothingToCompile, part.Side == PlsqlSide.Body
                ? "본문 쪽에는 본문(BODY) 소스를 둔다 — 명세는 「명세」 쪽에서 컴파일한다"
                : "명세 쪽에는 명세 소스를 둔다 — 본문은 「본문」 쪽에서 컴파일한다");
        }

        var owner = header.Value.Owner ?? part.Target?.Owner ?? _session.Profile.User.ToUpperInvariant();
        var effective = new DbObjectRef(owner, header.Value.Name, header.Value.ObjectType);
        if (part.Target is null || part.Target.Owner != effective.Owner || part.Target.Name != effective.Name || part.Target.ObjectType != effective.ObjectType)
        {
            // 이름·유형이 바뀌었거나 새 문서: 새 객체로 본다(충돌 기준 없음).
            part.Target = effective;
            part.Baseline = null;
            part.ServerSource = null;
            if (part.Editor.FilePath is null)
            {
                Title = $"{effective.Owner}.{effective.Name}"; // 파일 탭은 파일 이름을 그대로 둔다
            }

            RaiseSides();
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
            if (!force && meta is not null && part.Baseline is not null && await CheckServerChangedAsync(part, ct).ConfigureAwait(true))
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

            // 새 문서를 패키지·타입으로 처음 만들었으면 다른 쪽을 붙인다(처음 볼 때 서버에서 읽는다).
            BecomePair(part);
            IReadOnlyList<CompileError> errors = [];
            if (meta is not null)
            {
                part.Errors = errors = await ReadErrorsAsync(meta, _session, part, ct).ConfigureAwait(true);
                part.Baseline = await ReadLastDdlAsync(meta, _session, effective, ct).ConfigureAwait(true);
                PublishErrors(publish: true);
            }

            part.ServerSource = PlsqlSourceText.ToEditorText(text);
            part.Missing = false;
            part.Loaded = true;
            SetServerChanged(part, false);
            if (part.Editor.FilePath is null)
            {
                part.Editor.MarkSaved(); // 파일 탭은 컴파일해도 파일에는 쓰지 않았으므로 고침 표시를 지우지 않는다
            }

            RefreshMembers();
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

    /// <summary>
    /// 오류의 줄·열을 편집기 위치로 옮겨 커서를 둔다. 서버 오류는 소스 시작(유형 낱말)부터 센다.
    /// 패키지·타입이면 오류가 난 쪽(<see cref="CompileError.ObjectType"/>)으로 먼저 바꾼다.
    /// </summary>
    public bool NavigateTo(CompileError item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var part = PartOfError(item) ?? _current;
        var (lineOffset, colOffset) = PlsqlSourceText.SourceStart(part.Editor.Text);
        var line = item.Line + lineOffset;
        if (line < 1 || line > part.Editor.Document.LineCount)
        {
            return false;
        }

        if (!ReferenceEquals(part, _current))
        {
            ShowPart(part);
        }

        var dl = part.Editor.Document.GetLineByNumber(line);
        var col = item.Column + (item.Line == 1 ? colOffset + part.Line1Shift : 0);
        var offset = dl.Offset + Math.Clamp(col - 1, 0, dl.Length);
        part.Editor.Select(offset, 0);
        part.Editor.ScrollLine = line;
        part.Editor.Surface?.ScrollToLine(line);
        part.Editor.Surface?.FocusEditor();
        return true;
    }

    /// <summary>
    /// 서버 소스 줄 번호(<c>all_source.line</c>, 1부터)로 커서를 옮긴다(보이는 쪽). 앞에 붙인 <c>CREATE OR REPLACE</c>·머리 주석만큼 줄을 맞춘다.
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

    /// <summary>차이 보기 열기(보이는 쪽의 서버 소스를 새로 읽어 내 소스와 비교).</summary>
    public async Task ShowServerDiffAsync(CancellationToken ct = default)
    {
        var part = _current;
        if (part.Target is null || _session is null || ResolveMeta() is not { } meta)
        {
            return;
        }

        try
        {
            part.ServerSource = await meta.GetSourceAsync(_session, part.Target, ct).ConfigureAwait(true);
            if (!ReferenceEquals(part, _current))
            {
                return;
            }

            FillDiff(part, part.ServerSource);
            if (DiffText.Length == 0)
            {
                DiffText = "(차이 없음)";
            }
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
    /// 복원한 탭에 저장한 접속의 작업 세션을 붙이고, 저장한 <c>last_ddl_time</c> 과 서버를 견준다(다르면 충돌 배너). 글은 바꾸지 않는다.
    /// 패키지·타입에서 저장하지 않은 쪽(예전 형식의 한 쪽 탭)은 이때 서버에서 읽는다.
    /// 기다리는 탭이 아니거나 다른 프로필·작업 세션이 아닌 세션이면 붙이지 않고 false. 세션은 첫 await 앞에서 붙인다(두 번 붙지 않게).
    /// </summary>
    public async Task<bool> AttachSessionAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!AwaitingSession || session.Profile.Id != _restoredProfileId || session.Purpose != SessionPurpose.Work)
        {
            return false;
        }

        Session = session;
        if (Target is not { } target)
        {
            return true;
        }

        StatusText = $"{target.ObjectType} {target.Owner}.{target.Name}";
        IsBusy = true; // 견주는 동안 컴파일이 같은 세션에 겹치지 않게
        try
        {
            var gen = _generation;
            foreach (var part in Parts.ToList())
            {
                if (part.Loaded)
                {
                    await CheckServerChangedAsync(part, ct).ConfigureAwait(true);
                }
                else if (ResolveMeta() is { } meta)
                {
                    await LoadCompanionAsync(meta, session, part, gen, ct).ConfigureAwait(true);
                    PublishErrors(publish: false);
                    RefreshMembers();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = "서버 소스를 확인하지 못했다: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        RaiseSides();
        return true;
    }

    /// <summary>
    /// 탭을 닫을 때 저장 여부를 물어야 하는가(TSK-13-27, SQL 탭 규칙과 같다). 읽기 전용은 묻지 않는다. 패키지·타입은 어느 한 쪽이라도.
    /// 파일에 묶인 탭 = 파일과 달라졌을 때, 서버 객체를 열었거나 컴파일한 탭 = 마지막 읽기·컴파일 뒤로 고쳤을 때, 그 밖의 새 탭 = 사용자가 글을 쳤을 때(템플릿 그대로면 묻지 않는다).
    /// </summary>
    public bool NeedsSaveConfirm => Parts.Any(NeedsSave);

    private static bool NeedsSave(PlsqlSourcePart part) => part.Editor.NeedsSaveConfirm(serverBound: part.Target is not null);

    /// <summary>
    /// 닫기 확인: 저장 / 저장 안 함 / 취소를 묻고 닫아도 되면 true. 저장은 로컬 파일 저장이다(파일에 묶였으면 그 파일, 아니면 다른 이름으로).
    /// 패키지·타입은 고친 쪽마다 그 쪽을 보이고 차례로 묻는다(처음 취소에서 멈춘다). 서버에는 컴파일하지 않는다: 서버 반영은 명시적 컴파일로만 한다.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync(SqlEditorHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (var part in Parts.Where(NeedsSave).ToList())
        {
            if (!ReferenceEquals(part, _current))
            {
                ShowPart(part);
            }

            var name = _secondary is null ? Title : $"{Title} ({SideName(part.Side)})";
            if (!await host.AskAndSaveAsync(part.Editor, name, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>시험용: 문서 탭을 여는 방법(기본 = 셸 도킹).</summary>
    internal Func<PlsqlEditorViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>탭 제목: 소유자.이름(패키지·타입은 명세와 본문이 한 탭이라 쪽을 붙이지 않는다).</summary>
    private static string TitleOf(DbObjectRef target) => $"{target.Owner}.{target.Name}";

    private PlsqlCompileOutcome Report(PlsqlCompileStatus status, string message, IReadOnlyList<CompileError>? errors = null)
    {
        StatusText = message;
        return new PlsqlCompileOutcome(status, errors ?? [], message);
    }

    /// <summary>쪽의 오류를 읽는다. 패키지·타입이면 줄마다 그 쪽 유형을 적는다. 못 읽으면 빈 목록.</summary>
    private async Task<IReadOnlyList<CompileError>> ReadErrorsAsync(IMetadataProvider meta, ISession session, PlsqlSourcePart part, CancellationToken ct)
    {
        if (part.Target is not { } target)
        {
            return [];
        }

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

        return _secondary is null ? errors : [.. errors.Select(e => e with { ObjectType = target.ObjectType })];
    }

    /// <summary>
    /// 쪽마다 읽은 오류를 <see cref="Errors"/> 에 모아(명세 다음 본문) 컴파일 오류 창에 올린다.
    /// 컴파일 결과(<paramref name="publish"/>)는 늘 올린다. 열 때 읽은 목록은 이 탭이 활성일 때만(뒤에서 읽던 탭이 창을 가로채지 않게, 작업 공간 밖에서 쓰면 활성 탭이 없다).
    /// </summary>
    private void PublishErrors(bool publish)
    {
        Errors.Clear();
        foreach (var part in Parts.OrderBy(p => p.Side))
        {
            foreach (var e in part.Errors)
            {
                Errors.Add(e);
            }
        }

        if (publish || Active is null || ReferenceEquals(Active, this))
        {
            CompileErrorsViewModel.Publish(this);
        }
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
    private async Task<int> CountOtherUsersAsync(IExecutionService exec, ISession session, DbObjectRef target, CancellationToken ct)
    {
        static string Lit(string s) => s.Replace("'", "''", StringComparison.Ordinal);

        // 이 DBMS·버전에 문장이 없으면 null — 못 읽은 것과 같게 0(경고만 못 한다, 컴파일은 막지 않는다).
        var sql = CatalogRouting.Text(_services, session, "meta.object_users", ("owner", Lit(target.Owner)), ("name", Lit(target.Name)));
        if (sql is null)
        {
            return 0;
        }

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

    private ContextKeyService? Keys => _services?.GetService(typeof(ContextKeyService)) as ContextKeyService ?? ContextKeyService.Shared;

    /// <summary>이 탭의 문맥 키를 맞춘다. 활성 탭(<see cref="Active"/>)일 때만: 뒤에서 읽는 탭(미리보기·다른 작업 공간)이 키를 바꾸지 않게.</summary>
    private void SyncKeys()
    {
        if (!ReferenceEquals(Active, this) || Keys is not { } keys)
        {
            return;
        }

        keys.Set("isPackageDoc", IsPackageDoc);
        keys.Set("serverSourceChanged", ServerSourceChanged);
        keys.Set("plsqlRunnable", RunTarget is not null);
        // 편집 명령(oranda.edit.copy 등)은 포커스가 PL/SQL 편집기에 있으면 이 편집기(보이는 쪽)에 대고 돈다(SqlEditorCommandHandler).
        // hasSelection·canUndo·canRedo 는 SQL 탭 기준이라, 활성 조건이 이 편집기를 보도록 따로 둔다.
        keys.Set("plsqlHasSelection", Editor.HasSelection);
        keys.Set("plsqlCanUndo", Editor.CanUndo);
        keys.Set("plsqlCanRedo", Editor.CanRedo);
    }

    private static void ClearKeys(ContextKeyService? keys)
    {
        keys?.Set("isPackageDoc", false);
        keys?.Set("serverSourceChanged", false);
        keys?.Set("plsqlRunnable", false);
        keys?.Set("plsqlHasSelection", false);
        keys?.Set("plsqlCanUndo", false);
        keys?.Set("plsqlCanRedo", false);
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

    /// <summary>컴파일할 세션: 복원한 탭은 저장한 접속의 세션만(없으면 null), 그 밖에는 지금 접속.</summary>
    private ISession? ResolveSession()
        => _restoredProfileId is { } profile
            ? ConnectionManager.SessionRouter.ForProfile(_services, profile) is { Purpose: SessionPurpose.Work } own && own.Profile.Id == profile ? own : null
            : ConnectionManager.SessionRouter.CurrentOf(_services);
}
