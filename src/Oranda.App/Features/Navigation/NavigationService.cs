using System.Text;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Text;
using Oranda.Language.Navigation;

namespace Oranda.App.Features.Navigation;

public enum NavigationOutcome
{
    /// <summary>같은 문서 안 정의로 갔다.</summary>
    Local,

    /// <summary>DB 객체 DDL(객체 상세)을 열었다.</summary>
    OpenedDdl,

    /// <summary>PL/SQL 소스 탭을 열고 위치로 갔다.</summary>
    OpenedSource,

    NoSymbol,
    NoSession,
    NotFound,
    Failed,
}

public sealed record NavigationResult(NavigationOutcome Outcome, string Message, DbObjectRef? Target = null);

/// <summary>객체 정보 팝업 내용.</summary>
public sealed record ObjectInfo(string Title, IReadOnlyList<string> Lines);

/// <summary>참조 찾기 결과. <see cref="Index"/> = 선택한 참조.</summary>
public sealed record ReferencesResult(IReadOnlyList<TextRange> Ranges, int Index, ObjectInfo Summary);

/// <summary>편집 위치(뒤로·앞으로 이동용).</summary>
public sealed record EditorLocation(SqlEditorViewModel Tab, int Offset);

/// <summary>뒤로·앞으로 이동 기록(<c>oranda.nav.back</c>·<c>oranda.nav.forward</c>). UI 스레드에서만 쓴다.</summary>
public sealed class NavigationHistory
{
    private const int Limit = 100;
    private readonly List<EditorLocation> _back = [];
    private readonly List<EditorLocation> _forward = [];

    public static NavigationHistory Current { get; set; } = new();

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>이동하기 전 위치를 남긴다(앞으로 기록은 버린다).</summary>
    public void Push(EditorLocation from)
    {
        _back.Add(from);
        if (_back.Count > Limit)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
    }

    public EditorLocation? GoBack(EditorLocation current)
    {
        if (_back.Count == 0)
        {
            return null;
        }

        var loc = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(current);
        return loc;
    }

    public EditorLocation? GoForward(EditorLocation current)
    {
        if (_forward.Count == 0)
        {
            return null;
        }

        var loc = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(current);
        return loc;
    }
}

/// <summary>
/// 정의로 이동·객체 정보·참조 찾기 논리(TSK-04-06). 식별자 판별은 <see cref="ISymbolLocator"/>(L1 토큰), 문서 안 정의는 <see cref="SymbolAnalyzer"/>,
/// DB 쪽은 <see cref="IMetadataProvider"/>(캐시 우선, 모두 비동기라 UI 를 막지 않는다). DDL 은 객체 상세(TSK-07-03)가 연다.
/// 서비스는 쓸 때 꺼낸다(시작 경로에서 Oracle·ANTLR 어셈블리를 올리지 않기 위해).
/// </summary>
public sealed class NavigationService
{
    private static readonly string[] TypeOrder =
    [
        "TABLE", "VIEW", "MATERIALIZED VIEW", "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TYPE", "TRIGGER", "SEQUENCE", "SYNONYM",
    ];

    private static NavigationService? s_current;
    private readonly IServiceProvider? _services;

    public NavigationService(IServiceProvider? services = null) => _services = services;

    /// <summary>프로세스 안의 기본 서비스(명령 처리기·Ctrl+클릭이 함께 쓴다).</summary>
    public static NavigationService Current
    {
        get => s_current ??= new NavigationService();
        set => s_current = value;
    }

    public static NavigationService GetOrCreate(IServiceProvider? services)
    {
        if (s_current is null || (services is not null && s_current._services is null))
        {
            s_current = new NavigationService(services);
        }

        return s_current;
    }

    public SqlEditorHost Host { get; init; } = SqlEditorHost.Current;

    public NavigationHistory History { get; init; } = NavigationHistory.Current;

    /// <summary>시험용: 메타데이터 제공자(기본 = 서비스 컨테이너).</summary>
    internal IMetadataProvider? Metadata { get; set; }

    /// <summary>시험용: 편집기 탭의 세션(기본 = 같은 접속 프로필의 작업 세션).</summary>
    internal Func<SqlEditorViewModel, ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 객체 상세(DDL)를 여는 방법.</summary>
    internal Func<DbObjectRef, ISession, CancellationToken, Task>? OpenDdl { get; set; }

    private IMetadataProvider? Meta => Metadata ?? _services?.GetService(typeof(IMetadataProvider)) as IMetadataProvider;

    private ISession? SessionOf(SqlEditorViewModel tab)
    {
        if (SessionProvider is not null)
        {
            return SessionProvider(tab);
        }

        return ConnectionManager.SessionRouter.ForTab(_services, tab);
    }

    private SymbolReference? SymbolAt(string text, int offset)
        => (_services?.GetService(typeof(ISymbolLocator)) as ISymbolLocator)?.GetSymbolAt(text, offset) ?? SymbolAnalyzer.GetSymbolAt(text, offset);

    // ---- 정의로 이동 --------------------------------------------------------------------------

    public async Task<NavigationResult> GoToDefinitionAsync(SqlEditorViewModel tab, int offset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var text = tab.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var origin = new EditorLocation(tab, tab.CaretOffset);

        if (SymbolAnalyzer.FindLocalDefinition(text, offset) is { } local)
        {
            History.Push(origin);
            tab.Select(local.Start, local.Length);
            return new NavigationResult(NavigationOutcome.Local, $"{local.Name} 정의로 이동");
        }

        var sym = SymbolAt(text, offset);
        if (sym is null)
        {
            return new NavigationResult(NavigationOutcome.NoSymbol, "이동할 이름이 아니다");
        }

        var session = SessionOf(tab);
        var meta = Meta;
        if (session is null || meta is null)
        {
            return new NavigationResult(NavigationOutcome.NoSession, "접속이 있어야 DB 객체 정의로 이동한다");
        }

        try
        {
            var (obj, member) = await ResolveAsync(meta, session, sym, ct).ConfigureAwait(true);
            if (obj is null)
            {
                return new NavigationResult(NavigationOutcome.NotFound, $"{Display(sym)} 객체를 찾지 못했다");
            }

            if (obj.ObjectType is "PACKAGE" or "PACKAGE BODY" or "TYPE" or "TYPE BODY" or "PROCEDURE" or "FUNCTION" or "TRIGGER")
            {
                return await OpenSourceAsync(tab, meta, session, obj, member, origin, ct).ConfigureAwait(true);
            }

            History.Push(origin);
            await OpenDdlAsync(obj, session, ct).ConfigureAwait(true);
            return new NavigationResult(NavigationOutcome.OpenedDdl, $"{obj.Owner}.{obj.Name} DDL 열기", obj);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NavigationResult(NavigationOutcome.Failed, ex.Message);
        }
    }

    private Task OpenDdlAsync(DbObjectRef obj, ISession session, CancellationToken ct)
    {
        if (OpenDdl is not null)
        {
            return OpenDdl(obj, session, ct);
        }

        if (_services is null)
        {
            return Task.CompletedTask;
        }

        var handler = new ObjectDetailCommandHandler(_services) { SessionProvider = () => session };
        return handler.ExecuteAsync(new CommandContext("oranda.schema.showDdl", AlwaysKeys.Instance, obj), ct);
    }

    private async Task<NavigationResult> OpenSourceAsync(SqlEditorViewModel tab, IMetadataProvider meta, ISession session, DbObjectRef obj,
        string? member, EditorLocation origin, CancellationToken ct)
    {
        // 패키지·타입은 본문에 구현이 있으니 본문을 먼저, 없으면 명세.
        var attempts = new List<DbObjectRef>();
        if (obj.ObjectType is "PACKAGE" or "TYPE")
        {
            attempts.Add(obj with { ObjectType = obj.ObjectType + " BODY" });
        }

        attempts.Add(obj);

        string? source = null;
        DbObjectRef? used = null;
        TextRange? where = null;
        foreach (var a in attempts)
        {
            string s;
            try
            {
                s = await meta.GetSourceAsync(session, a, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (!ReferenceEquals(a, attempts[^1]))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            var loc = member is null ? null : SymbolAnalyzer.FindSubprogram(s, member);
            if (source is null || loc is not null)
            {
                source = s;
                used = a;
                where = loc;
            }

            if (member is null || loc is not null)
            {
                break;
            }
        }

        if (source is null || used is null)
        {
            return new NavigationResult(NavigationOutcome.NotFound, $"{obj.Owner}.{obj.Name} 소스가 없다");
        }

        var title = $"{used.Owner}.{used.Name}" + (used.ObjectType.EndsWith("BODY", StringComparison.Ordinal) ? " BODY" : string.Empty);
        History.Push(origin);
        var doc = Host.Tabs.FirstOrDefault(t => t.FilePath is null && t.IsReadOnly && t.Title == title && t.Text == source);
        if (doc is null)
        {
            doc = Host.NewDocument(source);
            doc.Title = title;
            doc.IsReadOnly = true;
            doc.ConnectionProfileId = tab.ConnectionProfileId;
            doc.IsDirty = false;
        }
        else
        {
            Host.Active = doc;
        }

        var r = where ?? new TextRange(0, 0);
        doc.Select(r.Start, r.Length);
        var msg = member is not null && where is null ? $"{title} 소스를 열었다({member} 위치는 찾지 못했다)" : $"{title} 소스로 이동";
        return new NavigationResult(NavigationOutcome.OpenedSource, msg, used);
    }

    // ---- 객체 정보 --------------------------------------------------------------------------

    public async Task<ObjectInfo?> DescribeAsync(SqlEditorViewModel tab, int offset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var text = tab.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var sym = SymbolAt(text, offset);
        if (sym is null)
        {
            return null;
        }

        var local = SymbolAnalyzer.FindLocalDefinition(text, offset);
        if (local is not null && local.Kind != LocalSymbolKind.TableAlias)
        {
            var line = LineOf(text, local.Start);
            return new ObjectInfo(local.Name, [$"{KindText(local.Kind)} · 정의: {line}줄", LineText(text, local.Start)]);
        }

        var session = SessionOf(tab);
        var meta = Meta;
        if (session is null || meta is null)
        {
            return new ObjectInfo(Display(sym), ["접속이 없어 DB 정보를 볼 수 없다"]);
        }

        try
        {
            var (obj, member) = await ResolveAsync(meta, session, sym, ct).ConfigureAwait(true);
            if (obj is null)
            {
                return new ObjectInfo(Display(sym), ["객체를 찾지 못했다"]);
            }

            var lines = new List<string>();
            var head = $"{obj.Owner}.{obj.Name}";
            if (obj.ObjectType is "TABLE" or "VIEW" or "MATERIALIZED VIEW")
            {
                var t = await meta.GetTableAsync(session, obj, ct).ConfigureAwait(true);
                lines.Add(obj.ObjectType + (string.IsNullOrEmpty(t.Comment) ? string.Empty : " — " + t.Comment));
                foreach (var c in t.Columns.OrderBy(c => c.Position))
                {
                    bool hit = member is not null && string.Equals(c.Name, member, StringComparison.OrdinalIgnoreCase);
                    lines.Add($"{(hit ? "> " : "  ")}{c.Name}  {TypeText(c)}{(c.IsNullable ? string.Empty : " NOT NULL")}{(string.IsNullOrEmpty(c.Comment) ? string.Empty : "  -- " + c.Comment)}");
                }

                if (member is not null && lines.Count > 0 && !lines.Any(l => l.StartsWith("> ", StringComparison.Ordinal)))
                {
                    lines.Insert(1, $"(열 {member} 을 찾지 못했다)");
                }
            }
            else if (obj.ObjectType is "PACKAGE" or "PACKAGE BODY" or "PROCEDURE" or "FUNCTION" or "TYPE")
            {
                lines.Add(obj.ObjectType);
                var procs = await meta.GetProceduresAsync(session, obj, ct).ConfigureAwait(true);
                foreach (var p in procs.Where(p => member is null || string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase)))
                {
                    lines.Add(Signature(p));
                }
            }
            else
            {
                lines.Add(obj.ObjectType);
            }

            return new ObjectInfo(head, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ObjectInfo(Display(sym), [ex.Message]);
        }
    }

    private static string TypeText(ColumnInfo c)
    {
        if (c.Precision is { } p)
        {
            return c.Scale is { } s and > 0 ? $"{c.DataType}({p},{s})" : $"{c.DataType}({p})";
        }

        return c.Length is { } l and > 0 && c.DataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ? $"{c.DataType}({l})" : c.DataType;
    }

    private static string Signature(ProcedureInfo p)
    {
        var sb = new StringBuilder(p.IsFunction ? "FUNCTION " : "PROCEDURE ").Append(p.Name).Append('(');
        sb.AppendJoin(", ", p.Arguments.Where(a => a.Position > 0 || !p.IsFunction)
            .Select(a => $"{a.Name} {(a.Direction == ArgumentDirection.In ? string.Empty : a.Direction == ArgumentDirection.Out ? "OUT " : "IN OUT ")}{a.DataType}"));
        sb.Append(')');
        if (p.ReturnType is not null)
        {
            sb.Append(" RETURN ").Append(p.ReturnType);
        }

        return sb.ToString();
    }

    private static string KindText(LocalSymbolKind k) => k switch
    {
        LocalSymbolKind.TableAlias => "테이블 별칭",
        LocalSymbolKind.DerivedAlias => "파생 테이블 별칭",
        LocalSymbolKind.Cte => "WITH 절(CTE)",
        LocalSymbolKind.Variable => "변수",
        LocalSymbolKind.Cursor => "커서",
        LocalSymbolKind.Subprogram => "로컬 프로시저·함수",
        LocalSymbolKind.Parameter => "매개변수",
        LocalSymbolKind.Type => "형식",
        _ => "반복 변수",
    };

    // ---- 참조 찾기 --------------------------------------------------------------------------

    /// <summary>같은 문서 안 참조를 찾고, 커서 다음 참조를 선택한다(끝이면 처음으로).</summary>
    public static ReferencesResult? FindReferences(SqlEditorViewModel tab, int offset)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var text = tab.Text;
        var refs = SymbolAnalyzer.FindReferences(text, Math.Clamp(offset, 0, text.Length));
        if (refs.Count == 0)
        {
            return null;
        }

        int idx = Math.Max(0, refs.ToList().FindIndex(r => r.Start > offset));

        var lines = new List<string> { $"{refs.Count}개 참조" };
        lines.AddRange(refs.Select(r => $"{LineOf(text, r.Start)}줄: {LineText(text, r.Start)}"));
        tab.Select(refs[idx].Start, refs[idx].Length);
        return new ReferencesResult(refs, idx, new ObjectInfo(text.Substring(refs[0].Start, refs[0].Length), lines));
    }

    // ---- 뒤로·앞으로 ------------------------------------------------------------------------

    public bool Back() => Move(back: true);

    public bool Forward() => Move(back: false);

    private bool Move(bool back)
    {
        var cur = Host.Active;
        if (cur is null)
        {
            return false;
        }

        var here = new EditorLocation(cur, cur.CaretOffset);
        var to = back ? History.GoBack(here) : History.GoForward(here);
        // 닫힌 탭 기록은 건너뛴다.
        while (to is not null && !Host.Tabs.Contains(to.Tab))
        {
            to = back ? History.GoBack(here) : History.GoForward(here);
        }

        if (to is null)
        {
            return false;
        }

        Host.Active = to.Tab;
        to.Tab.Select(Math.Min(to.Offset, to.Tab.Document.TextLength), 0);
        return true;
    }

    // ---- 객체 찾기 --------------------------------------------------------------------------

    internal static async Task<(DbObjectRef? Obj, string? Member)> ResolveAsync(IMetadataProvider meta, ISession session, SymbolReference sym, CancellationToken ct)
    {
        var current = session.Profile.User.ToUpperInvariant();
        if (sym.Owner is not null)
        {
            return (await FindAsync(meta, session, sym.Owner, sym.Name, current, ct).ConfigureAwait(true), sym.Member);
        }

        var obj = await FindAsync(meta, session, null, sym.Name, current, ct).ConfigureAwait(true);
        if (obj is null && sym.Member is not null)
        {
            // pkg.proc 로 읽었지만 사실 schema.object 였던 경우.
            obj = await FindAsync(meta, session, sym.Name, sym.Member, current, ct).ConfigureAwait(true);
            return (obj, null);
        }

        return (obj, sym.Member);
    }

    private static async Task<DbObjectRef?> FindAsync(IMetadataProvider meta, ISession session, string? owner, string name, string current, CancellationToken ct)
    {
        var list = await meta.ListObjectsAsync(session, new ObjectQuery { Owner = owner, NamePattern = name, Limit = 200 }, ct).ConfigureAwait(true);
        return list
            .Where(o => string.Equals(o.Name, name, StringComparison.Ordinal))
            .OrderBy(o => string.Equals(o.Owner, current, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => TypeRank(o.ObjectType))
            .FirstOrDefault();
    }

    private static int TypeRank(string type)
    {
        var i = Array.IndexOf(TypeOrder, type);
        return i < 0 ? TypeOrder.Length : i;
    }

    private static string Display(SymbolReference s)
        => string.Join('.', new[] { s.Owner, s.Name, s.Member }.Where(x => !string.IsNullOrEmpty(x)));

    private static int LineOf(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string LineText(string text, int offset)
    {
        int s = offset == 0 ? 0 : text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1) + 1;
        int e = text.IndexOf('\n', Math.Min(offset, text.Length));
        if (e < 0)
        {
            e = text.Length;
        }

        return text[s..e].Trim().TrimEnd('\r');
    }

    private sealed class AlwaysKeys : IContextKeys
    {
        public static readonly AlwaysKeys Instance = new();

        public bool Evaluate(string? expression) => true;
    }
}
