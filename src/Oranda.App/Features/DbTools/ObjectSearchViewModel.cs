using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DbTools;

/// <summary>검색 결과 한 줄(화면용).</summary>
public sealed class ObjectSearchItem
{
    public ObjectSearchItem(ObjectSearchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
    }

    public ObjectSearchRow Row { get; }

    public DbObjectRef Ref => Row.Ref;

    public string Title => $"{Ref.Owner}.{Ref.Name}";

    public string ObjectType => Ref.ObjectType;

    public string Status => Row.Status;

    public bool IsInvalid => !string.Equals(Row.Status, "VALID", StringComparison.Ordinal);

    public string Created => Row.Created?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public string Modified => Row.LastDdlTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public string Size => DbToolsSupport.FormatSize(Row.SizeBytes);
}

/// <summary>INVALID 일괄 컴파일 결과 한 줄.</summary>
public enum CompileOutcome
{
    /// <summary>컴파일 뒤 VALID.</summary>
    Compiled,

    /// <summary>컴파일했지만 여전히 INVALID(컴파일 오류 있음).</summary>
    StillInvalid,

    /// <summary>실행 엔진이 실패·거부·취소.</summary>
    Failed,

    /// <summary>컴파일할 수 없는 유형.</summary>
    Skipped,
}

public sealed record CompileResultItem(DbObjectRef Ref, CompileOutcome Outcome, string? Message)
{
    public string Title => $"{Ref.ObjectType} {Ref.Owner}.{Ref.Name}";

    public string OutcomeText => Outcome switch
    {
        CompileOutcome.Compiled => "컴파일됨",
        CompileOutcome.StillInvalid => "여전히 INVALID",
        CompileOutcome.Skipped => "건너뜀",
        _ => "실패",
    };

    public string Line => Message is null ? $"{OutcomeText}  {Title}" : $"{OutcomeText}  {Title} — {Message}";
}

/// <summary>휴지통 항목 한 줄(화면용).</summary>
public sealed class RecycleBinRow
{
    public RecycleBinRow(RecycleBinItem item) => Item = item ?? throw new ArgumentNullException(nameof(item));

    public RecycleBinItem Item { get; }

    public string Title => $"{Item.OriginalName}  ({Item.ObjectType})";

    public string Dropped => Item.DropTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

    public string Flags => (Item.CanUndrop ? "되살리기 " : string.Empty) + (Item.CanPurge ? "비우기" : string.Empty);
}

/// <summary>
/// <c>oranda.panel.objectSearch</c> — 객체 검색(이름·유형·크기·생성일·변경일, 참조 관계)과 INVALID 일괄 컴파일·휴지통 Purge/Flashback(TSK-07-04).
/// 읽기는 <see cref="ISchemaAdminProvider"/>, 상태를 바꾸는 문장(컴파일·Purge·Flashback)은 반드시 <see cref="IExecutionService"/> 로 보낸다
/// (정책 관문·위험 명령 확인·읽기 전용 접속 차단은 그쪽 몫). 서비스는 처음 쓸 때 꺼낸다.
/// </summary>
[OrandaToolPanel("oranda.panel.objectSearch", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class ObjectSearchViewModel : ToolPanelViewModelBase
{
    /// <summary>한 번에 컴파일을 되풀이하는 최대 횟수(의존 순서 때문에 한 번에 안 되는 객체가 있다).</summary>
    internal const int MaxCompilePasses = 3;

    public static IReadOnlyList<string> SearchableTypes { get; } =
    [
        "TABLE", "VIEW", "MATERIALIZED VIEW", "INDEX", "SEQUENCE", "SYNONYM", "PROCEDURE", "FUNCTION", "PACKAGE", "PACKAGE BODY",
        "TYPE", "TYPE BODY", "TRIGGER", "DATABASE LINK",
    ];

    private readonly IServiceProvider? _services;
    private ISchemaAdminProvider? _admin;
    private IExecutionService? _exec;
    private int _generation;

    public ObjectSearchViewModel()
    {
    }

    public ObjectSearchViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 서비스를 직접 넣는다.</summary>
    internal ObjectSearchViewModel(ISchemaAdminProvider admin, IExecutionService exec)
    {
        _admin = admin;
        _exec = exec;
    }

    public ISession? Session { get; set; }

    public ObservableCollection<ObjectSearchItem> Results { get; } = [];

    public ObservableCollection<CompileResultItem> CompileResults { get; } = [];

    public ObservableCollection<RecycleBinRow> RecycleBin { get; } = [];

    [ObservableProperty]
    private string _owner = string.Empty;

    /// <summary>이름 조건(<c>*</c>·<c>%</c> 와일드카드, 없으면 「포함」).</summary>
    [ObservableProperty]
    private string _nameText = string.Empty;

    /// <summary>빈 문자열 = 모든 유형.</summary>
    [ObservableProperty]
    private string? _typeFilter;

    [ObservableProperty]
    private bool _invalidOnly;

    [ObservableProperty]
    private DateTime? _createdAfter;

    [ObservableProperty]
    private DateTime? _modifiedAfter;

    [ObservableProperty]
    private decimal? _minSizeKb;

    [ObservableProperty]
    private decimal? _maxSizeKb;

    [ObservableProperty]
    private ObjectSearchItem? _selectedResult;

    [ObservableProperty]
    private string _referencesText = string.Empty;

    [ObservableProperty]
    private RecycleBinRow? _selectedRecycle;

    /// <summary>휴지통 항목을 되살릴 때 새 이름(비우면 원래 이름).</summary>
    [ObservableProperty]
    private string _flashbackName = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool CanChange => Session is { Profile.ReadOnly: false };

    partial void OnSelectedResultChanged(ObjectSearchItem? value)
    {
        ReferencesText = string.Empty;
        if (value is not null && Session is not null)
        {
            _ = LoadReferencesAsync(value);
        }
    }

    /// <summary>접속을 정하고 스키마 기본값을 채운다.</summary>
    public void Attach(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        if (string.IsNullOrWhiteSpace(Owner))
        {
            Owner = DbToolsSupport.DefaultOwner(session) ?? string.Empty;
        }

        OnPropertyChanged(nameof(CanChange));
    }

    public ObjectSearchQuery BuildQuery() => new()
    {
        Owner = string.IsNullOrWhiteSpace(Owner) ? null : Owner.Trim().ToUpperInvariant(),
        ObjectTypes = string.IsNullOrWhiteSpace(TypeFilter) ? [] : [TypeFilter.Trim()],
        NamePattern = SchemaFilter.ToLikePattern(NameText),
        InvalidOnly = InvalidOnly,
        CreatedAfter = CreatedAfter?.Date,
        ModifiedAfter = ModifiedAfter?.Date,
        MinSizeBytes = MinSizeKb is { } min ? (long)(min * 1024) : null,
        MaxSizeBytes = MaxSizeKb is { } max ? (long)(max * 1024) : null,
    };

    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (Session is not { } session || ResolveAdmin() is not { } admin)
        {
            StatusText = Session is null ? "접속이 없다" : "검색 제공자가 없다";
            return;
        }

        var gen = Interlocked.Increment(ref _generation);
        IsBusy = true;
        try
        {
            var rows = await admin.SearchObjectsAsync(session, BuildQuery(), ct);
            if (gen != _generation)
            {
                return;
            }

            Results.Clear();
            foreach (var r in rows)
            {
                Results.Add(new ObjectSearchItem(r));
            }

            StatusText = rows.Count == 0 ? "조건에 맞는 객체가 없다" : $"{rows.Count}개 찾음";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "검색하지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>선택한 객체의 참조 관계(쓰는 것·쓰이는 곳)를 읽는다.</summary>
    public async Task LoadReferencesAsync(ObjectSearchItem item, CancellationToken ct = default)
    {
        if (Session is not { } session || ResolveAdmin() is not { } admin)
        {
            return;
        }

        try
        {
            var refs = await admin.GetReferencesAsync(session, item.Ref, ct);
            if (!ReferenceEquals(SelectedResult, item))
            {
                return;
            }

            var sb = new StringBuilder();
            foreach (var (title, used) in new[] { ("이 객체가 쓰는 것", false), ("이 객체를 쓰는 것", true) })
            {
                sb.AppendLine(title);
                var list = refs.Where(r => r.IsUsedBy == used).ToList();
                if (list.Count == 0)
                {
                    sb.AppendLine("  (없음)");
                }

                foreach (var r in list)
                {
                    sb.Append("  ").Append(r.Ref.ObjectType).Append(' ').Append(r.Ref.Owner).Append('.').AppendLine(r.Ref.Name);
                }
            }

            ReferencesText = sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReferencesText = "참조 관계를 읽지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>
    /// 스키마의 INVALID 객체를 모두 컴파일한다. 객체마다 <c>ALTER … COMPILE</c> 을 실행 엔진으로 보내고, 의존 순서 때문에 남는 것은
    /// 진전이 없을 때까지(최대 <see cref="MaxCompilePasses"/> 번) 다시 한다. 끝나면 서버에서 INVALID 를 다시 읽어 결과를 낸다.
    /// </summary>
    public async Task CompileInvalidAsync(CancellationToken ct = default)
    {
        if (Session is not { } session || ResolveAdmin() is not { } admin || ResolveExec() is not { } exec)
        {
            StatusText = Session is null ? "접속이 없다" : "컴파일에 필요한 서비스가 없다";
            return;
        }

        if (!CanChange)
        {
            StatusText = "읽기 전용 접속에서는 컴파일할 수 없다";
            return;
        }

        var owner = string.IsNullOrWhiteSpace(Owner) ? DbToolsSupport.DefaultOwner(session) : Owner.Trim().ToUpperInvariant();
        var query = new ObjectSearchQuery { Owner = owner, InvalidOnly = true, Limit = 0 };
        IsBusy = true;
        CompileResults.Clear();
        try
        {
            var initial = (await admin.SearchObjectsAsync(session, query, ct)).Select(r => r.Ref).ToList();
            if (initial.Count == 0)
            {
                StatusText = "INVALID 객체가 없다";
                return;
            }

            var failures = new Dictionary<DbObjectRef, string>();
            var stopped = false;
            string? stopMessage = null;
            var pending = initial;
            for (var pass = 1; pass <= MaxCompilePasses && pending.Count > 0 && !stopped; pass++)
            {
                foreach (var obj in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    var stmt = BuildCompile(obj);
                    if (stmt is null)
                    {
                        continue;
                    }

                    var outcome = await DbToolsSupport.RunAsync(exec, session, stmt, ct);
                    if (outcome.Succeeded)
                    {
                        failures.Remove(obj);
                        continue;
                    }

                    failures[obj] = outcome.Message ?? "실패";

                    // 거부·취소면 더 보내지 않는다(같은 이유로 계속 막힌다).
                    if (outcome.Status is ExecutionStatus.Denied or ExecutionStatus.Cancelled)
                    {
                        stopped = true;
                        stopMessage = outcome.Message;
                        break;
                    }
                }

                var remaining = (await admin.SearchObjectsAsync(session, query, ct)).Select(r => r.Ref).ToList();
                var progressed = remaining.Count < pending.Count;
                pending = remaining;
                if (!progressed)
                {
                    break;
                }
            }

            var stillInvalid = (await admin.SearchObjectsAsync(session, query, ct)).Select(r => r.Ref).ToHashSet();
            foreach (var obj in initial)
            {
                CompileResults.Add(Classify(obj, stillInvalid.Contains(obj), failures.GetValueOrDefault(obj) ?? (stopped ? stopMessage : null)));
            }

            var ok = CompileResults.Count(r => r.Outcome == CompileOutcome.Compiled);
            StatusText = $"INVALID {initial.Count}개 중 {ok}개 컴파일됨, {initial.Count - ok}개 남음";
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
        }
        catch (Exception ex)
        {
            StatusText = "컴파일하지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static CompileResultItem Classify(DbObjectRef obj, bool stillInvalid, string? failure)
    {
        if (BuildCompile(obj) is null)
        {
            return new CompileResultItem(obj, CompileOutcome.Skipped, "컴파일할 수 없는 유형");
        }

        if (!stillInvalid)
        {
            return new CompileResultItem(obj, CompileOutcome.Compiled, null);
        }

        // 컴파일 오류가 있으면 서버는 「오류가 있는 채로 컴파일됨」(ORA-24344)을 알린다 — 실패가 아니라 INVALID 로 남은 것이다.
        return failure is not null && !failure.Contains("24344", StringComparison.Ordinal)
            ? new CompileResultItem(obj, CompileOutcome.Failed, failure)
            : new CompileResultItem(obj, CompileOutcome.StillInvalid, "컴파일 오류가 있다(PL/SQL 소스 편집기·컴파일 오류 창에서 확인)");
    }

    /// <summary>현재 스키마의 휴지통을 읽는다.</summary>
    public async Task LoadRecycleBinAsync(CancellationToken ct = default)
    {
        if (Session is not { } session || ResolveAdmin() is not { } admin)
        {
            StatusText = Session is null ? "접속이 없다" : "휴지통 제공자가 없다";
            return;
        }

        try
        {
            var items = await admin.ListRecycleBinAsync(session, ct);
            RecycleBin.Clear();
            foreach (var i in items)
            {
                RecycleBin.Add(new RecycleBinRow(i));
            }

            StatusText = items.Count == 0 ? "휴지통이 비었다" : $"휴지통 {items.Count}개";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "휴지통을 읽지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>항목 하나를 영구 삭제한다(이름 재입력 확인은 정책 관문이 한다). 항목을 안 주면 선택 항목.</summary>
    public Task PurgeAsync(RecycleBinItem? item = null, CancellationToken ct = default)
        => ChangeRecycleBinAsync(item ?? SelectedRecycle?.Item, static i => BuildPurge(i), "비웠다", ct);

    /// <summary>휴지통 전체를 비운다.</summary>
    public Task PurgeAllAsync(CancellationToken ct = default)
        => ChangeRecycleBinAsync(null, _ => BuildPurgeAll(), "휴지통을 모두 비웠다", ct, allowNoItem: true);

    /// <summary>항목을 되살린다(<see cref="FlashbackName"/> 이 있으면 그 이름으로).</summary>
    public Task FlashbackAsync(RecycleBinItem? item = null, CancellationToken ct = default)
    {
        var rename = string.IsNullOrWhiteSpace(FlashbackName) ? null : FlashbackName.Trim();
        return ChangeRecycleBinAsync(item ?? SelectedRecycle?.Item, i => BuildFlashback(i, rename), "되살렸다", ct);
    }

    private async Task ChangeRecycleBinAsync(RecycleBinItem? item, Func<RecycleBinItem, SqlStatement> build, string doneText,
                                             CancellationToken ct, bool allowNoItem = false)
    {
        if (Session is not { } session || ResolveExec() is not { } exec)
        {
            StatusText = Session is null ? "접속이 없다" : "실행 엔진이 없다";
            return;
        }

        if (!CanChange)
        {
            StatusText = "읽기 전용 접속에서는 바꿀 수 없다";
            return;
        }

        if (item is null && !allowNoItem)
        {
            StatusText = "휴지통 항목을 고르세요";
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await DbToolsSupport.RunAsync(exec, session, build(item!), ct);
            StatusText = outcome.Succeeded ? doneText : (outcome.Status == ExecutionStatus.Denied ? "막힘: " : "실패: ") + outcome.Message;
            if (outcome.Succeeded)
            {
                await LoadRecycleBinAsync(ct);
                StatusText = doneText;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
        }
        catch (Exception ex)
        {
            StatusText = "실행하지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Oracle 어셈블리 형식은 아래 작은 메서드 안에서만 쓴다(처음 부를 때 올라온다).
    private static SqlStatement? BuildCompile(DbObjectRef obj) => Oranda.Oracle.Schema.SchemaStatements.Compile(obj);

    private static SqlStatement BuildPurge(RecycleBinItem item) => Oranda.Oracle.Schema.SchemaStatements.PurgeItem(item);

    private static SqlStatement BuildPurgeAll() => Oranda.Oracle.Schema.SchemaStatements.PurgeAll();

    private static SqlStatement BuildFlashback(RecycleBinItem item, string? rename) => Oranda.Oracle.Schema.SchemaStatements.Flashback(item, rename);

    private ISchemaAdminProvider? ResolveAdmin()
        => _admin ??= (ISchemaAdminProvider?)_services?.GetService(typeof(ISchemaAdminProvider));

    private IExecutionService? ResolveExec()
        => _exec ??= (IExecutionService?)_services?.GetService(typeof(IExecutionService));
}
