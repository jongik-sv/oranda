using System.Globalization;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Language.Completion;

/// <summary>
/// 자동완성 후보(EDIT-04). 문맥(<see cref="ILanguageService.GetCompletionContext"/>) + <see cref="IMetadataProvider"/> 로 후보를 만든다.
/// 메타데이터 조회가 <see cref="DefaultBudget"/>(150 ms) 안에 끝나지 않으면 메모리 캐시에 이미 있는 것만으로 답하고
/// <see cref="CompletionResult.IsPartial"/> = true. 늦은 조회는 뒤에서 마저 끝나 메모리 캐시를 채운다(다음 호출이 바로 쓴다).
/// 소유: TSK-04-05.
/// </summary>
[OrandaService(typeof(ICompletionProvider))]
internal sealed class CompletionProvider : ICompletionProvider, IDisposable
{
    /// <summary>PRD 「자동완성 목록 표시 150 ms 이하」.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>한 번에 돌려주는 후보 수 한도(목록 창이 무거워지지 않게).</summary>
    internal const int MaxItems = 1000;

    private readonly LanguageService _language = new();
    private readonly MetadataLookup _lookup;
    private readonly TimeSpan _budget;
    private readonly IDisposable? _subscription;

    public CompletionProvider(IMetadataProvider metadata)
        : this(metadata, null, DefaultBudget)
    {
    }

    public CompletionProvider(IMetadataProvider metadata, IEventBus events)
        : this(metadata, events, DefaultBudget)
    {
    }

    /// <param name="budget">메타데이터 조회를 기다리는 최대 시간(시험용으로 바꾼다).</param>
    internal CompletionProvider(IMetadataProvider metadata, IEventBus? events, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _lookup = new MetadataLookup(metadata);
        _budget = budget;
        _subscription = events?.Subscribe<MetadataInvalidated>(this, e => _lookup.Invalidate(e.DbKey));
    }

    internal MetadataLookup Lookup => _lookup;

    public void Dispose() => _subscription?.Dispose();

    public async Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ctx = _language.GetCompletionContext(text, offset);
        if (ctx.Kind == CompletionContextKind.None)
        {
            return new CompletionResult([], ctx.ReplaceRange);
        }

        var items = new List<CompletionItem>();
        AddWords(ctx, items);

        bool partial = false;
        if (session is not null && ctx.Kind is not (CompletionContextKind.Keyword or CompletionContextKind.Hint))
        {
            var full = BuildAsync(new Source(_lookup, session, Peek: false), ctx);
            if (!full.IsCompleted)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(_budget, delayCts.Token);
                await Task.WhenAny(full, delay).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            if (full.IsCompleted)
            {
                items.InsertRange(0, await full.ConfigureAwait(false));
            }
            else
            {
                partial = true;
                items.InsertRange(0, await BuildAsync(new Source(_lookup, session, Peek: true), ctx).ConfigureAwait(false));
            }
        }

        bool lower = PrefersLowerCase(text, ctx);
        var result = new List<CompletionItem>(Math.Min(items.Count, MaxItems));
        var seen = new HashSet<(string, CompletionItemKind)>();
        foreach (var item in items)
        {
            if (!item.Label.StartsWith(ctx.Prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add((item.Label, item.Kind)))
            {
                continue;
            }

            result.Add(item with { InsertText = Format(item.InsertText, item.Kind, lower) });
            if (result.Count >= MaxItems)
            {
                break;
            }
        }

        return new CompletionResult(result, ctx.ReplaceRange) { IsPartial = partial };
    }

    // ---- DB 없는 후보 -------------------------------------------------------------------------

    private static void AddWords(CompletionContext ctx, List<CompletionItem> items)
    {
        switch (ctx.Kind)
        {
            case CompletionContextKind.Hint:
                Add(items, CompletionWords.Hints, CompletionItemKind.Hint, "옵티마이저 힌트");
                break;
            case CompletionContextKind.Keyword:
                Add(items, CompletionWords.StatementStart, CompletionItemKind.Keyword, null);
                Add(items, CompletionWords.Clause, CompletionItemKind.Keyword, null);
                break;
            case CompletionContextKind.Column when ctx.Qualifier is null:
                Add(items, CompletionWords.Functions, CompletionItemKind.Function, "내장 함수");
                Add(items, CompletionWords.Clause, CompletionItemKind.Keyword, null);
                break;
        }
    }

    private static void Add(List<CompletionItem> items, string[] words, CompletionItemKind kind, string? detail)
    {
        foreach (var w in words)
        {
            items.Add(new CompletionItem(w, kind, w) { Detail = detail });
        }
    }

    // ---- 메타데이터 후보 ----------------------------------------------------------------------

    /// <summary>조회 방법. <see cref="Peek"/> 면 메모리에 끝난 결과만 쓰고 기다리지 않는다.</summary>
    private sealed record Source(MetadataLookup Lookup, ISession Session, bool Peek)
    {
        public string DefaultOwner { get; } =
            (Session.Server.SessionUser ?? Session.Profile.User).ToUpperInvariant();

        public Task<IReadOnlyList<string>?> SchemasAsync()
            => Peek ? Task.FromResult(PeekOrNull<IReadOnlyList<string>>(MetadataLookup.SchemasKey(Session)))
                    : Safe(Lookup.SchemasAsync(Session));

        public Task<IReadOnlyList<DbObjectRef>?> ObjectsAsync(string owner)
            => Peek ? Task.FromResult(PeekOrNull<IReadOnlyList<DbObjectRef>>(MetadataLookup.ObjectsKey(Session, owner)))
                    : Safe(Lookup.ObjectsAsync(Session, owner));

        public Task<TableInfo?> TableAsync(DbObjectRef table)
            => Peek ? Task.FromResult(PeekOrNull<TableInfo>(MetadataLookup.TableKey(Session, table.Owner, table.Name)))
                    : Safe(Lookup.TableAsync(Session, table));

        public Task<IReadOnlyList<ProcedureInfo>?> ProceduresAsync(DbObjectRef obj)
            => Peek ? Task.FromResult(PeekOrNull<IReadOnlyList<ProcedureInfo>>(MetadataLookup.ProceduresKey(Session, obj.Owner, obj.Name)))
                    : Safe(Lookup.ProceduresAsync(Session, obj));

        private T? PeekOrNull<T>(string key)
            where T : class => Lookup.TryPeek<T>(key, out var v) ? v : null;

        private static async Task<T?> Safe<T>(Task<T> task)
            where T : class
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return null; // 없는 객체·권한 없음·끊김: 후보 없이 넘어간다
            }
        }
    }

    private static async Task<List<CompletionItem>> BuildAsync(Source src, CompletionContext ctx)
    {
        var items = new List<CompletionItem>();
        switch (ctx.Kind)
        {
            case CompletionContextKind.TableOrView:
                {
                    var owner = ctx.Qualifier is { } q && !q.Contains('.', StringComparison.Ordinal) ? q : src.DefaultOwner;
                    AddObjects(items, await src.ObjectsAsync(owner).ConfigureAwait(false), tablesOnly: true);
                    if (ctx.Qualifier is null)
                    {
                        AddSchemas(items, await src.SchemasAsync().ConfigureAwait(false));
                    }

                    break;
                }

            case CompletionContextKind.Schema:
                AddSchemas(items, await src.SchemasAsync().ConfigureAwait(false));
                break;

            case CompletionContextKind.Column when ctx.Qualifier is { } q:
                {
                    var name = ctx.Aliases.TryGetValue(q, out var target) ? target : q;
                    await AddColumnsAsync(src, name, items, withTable: false).ConfigureAwait(false);
                    break;
                }

            case CompletionContextKind.Column:
                foreach (var table in ctx.Aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await AddColumnsAsync(src, table, items, withTable: true).ConfigureAwait(false);
                }

                break;

            case CompletionContextKind.PackageMember when ctx.Qualifier is { } q:
                await AddMembersAsync(src, q, items).ConfigureAwait(false);
                break;
        }

        return items;
    }

    private static (string Owner, string Name) Split(string qualified, string defaultOwner)
    {
        int dot = qualified.LastIndexOf('.');
        return dot < 0 ? (defaultOwner, qualified) : (qualified[..dot], qualified[(dot + 1)..]);
    }

    private static async Task AddColumnsAsync(Source src, string qualifiedName, List<CompletionItem> items, bool withTable)
    {
        var (owner, name) = Split(qualifiedName, src.DefaultOwner);
        if (owner.Contains('.', StringComparison.Ordinal))
        {
            return; // 이름이 셋 이상(owner.table@link 등) — 모른다
        }

        // 유형을 알면 그대로(뷰), 모르면 TABLE 로 묻는다(제공자는 이름으로 열을 찾는다).
        var objects = await src.ObjectsAsync(owner).ConfigureAwait(false);
        var type = objects?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal)
            && o.ObjectType is "TABLE" or "VIEW" or "MATERIALIZED VIEW")?.ObjectType ?? "TABLE";
        var info = await src.TableAsync(new DbObjectRef(owner, name, type)).ConfigureAwait(false);
        if (info is null)
        {
            return;
        }

        foreach (var c in info.Columns.OrderBy(c => c.Position))
        {
            var detail = ColumnDetail(c);
            items.Add(new CompletionItem(c.Name, CompletionItemKind.Column, c.Name) { Detail = withTable ? name + " · " + detail : detail });
        }
    }

    private static async Task AddMembersAsync(Source src, string qualifier, List<CompletionItem> items)
    {
        if (qualifier.Contains('.', StringComparison.Ordinal))
        {
            var (owner, name) = Split(qualifier, src.DefaultOwner);
            AddProcedures(items, await src.ProceduresAsync(new DbObjectRef(owner, name, "PACKAGE")).ConfigureAwait(false));
            return;
        }

        // 1) 내 스키마의 패키지
        var mine = await src.ObjectsAsync(src.DefaultOwner).ConfigureAwait(false);
        if (mine?.Any(o => o.Name == qualifier && o.ObjectType == "PACKAGE") == true)
        {
            AddProcedures(items, await src.ProceduresAsync(new DbObjectRef(src.DefaultOwner, qualifier, "PACKAGE")).ConfigureAwait(false));
            return;
        }

        // 2) 스키마 이름이면 그 스키마의 객체
        var schemas = await src.SchemasAsync().ConfigureAwait(false);
        if (schemas?.Contains(qualifier, StringComparer.Ordinal) == true)
        {
            AddObjects(items, await src.ObjectsAsync(qualifier).ConfigureAwait(false), tablesOnly: false);
            return;
        }

        // 3) 공용 시노님 대상인 SYS 패키지(DBMS_OUTPUT 등)
        AddProcedures(items, await src.ProceduresAsync(new DbObjectRef("SYS", qualifier, "PACKAGE")).ConfigureAwait(false));
    }

    private static void AddObjects(List<CompletionItem> items, IReadOnlyList<DbObjectRef>? objects, bool tablesOnly)
    {
        if (objects is null)
        {
            return;
        }

        foreach (var o in objects)
        {
            CompletionItemKind? kind = o.ObjectType switch
            {
                "TABLE" => CompletionItemKind.Table,
                "VIEW" or "MATERIALIZED VIEW" => CompletionItemKind.View,
                "SYNONYM" => CompletionItemKind.Synonym,
                "PACKAGE" when !tablesOnly => CompletionItemKind.Package,
                "PROCEDURE" when !tablesOnly => CompletionItemKind.Procedure,
                "FUNCTION" when !tablesOnly => CompletionItemKind.Function,
                "SEQUENCE" when !tablesOnly => CompletionItemKind.Keyword,
                _ => null,
            };
            if (kind is { } k)
            {
                items.Add(new CompletionItem(o.Name, k, o.Name) { Detail = o.ObjectType + " · " + o.Owner });
            }
        }
    }

    private static void AddSchemas(List<CompletionItem> items, IReadOnlyList<string>? schemas)
    {
        if (schemas is null)
        {
            return;
        }

        foreach (var s in schemas)
        {
            items.Add(new CompletionItem(s, CompletionItemKind.Schema, s) { Detail = "스키마" });
        }
    }

    private static void AddProcedures(List<CompletionItem> items, IReadOnlyList<ProcedureInfo>? procs)
    {
        if (procs is null)
        {
            return;
        }

        foreach (var group in procs.GroupBy(p => p.Name, StringComparer.Ordinal))
        {
            var first = group.First();
            var detail = string.Join('\n', group.Select(Signature));
            items.Add(new CompletionItem(first.Name, first.IsFunction ? CompletionItemKind.Function : CompletionItemKind.Procedure, first.Name) { Detail = detail });
        }
    }

    /// <summary>인자 목록 표기: <c>(P_ID IN NUMBER, P_NAME IN VARCHAR2 := …) RETURN NUMBER</c>.</summary>
    internal static string Signature(ProcedureInfo p)
    {
        var sb = new StringBuilder(p.Name).Append('(');
        bool first = true;
        foreach (var a in p.Arguments.Where(a => !string.IsNullOrEmpty(a.Name)).OrderBy(a => a.Position))
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append(a.Name).Append(' ').Append(a.Direction switch
            {
                ArgumentDirection.Out => "OUT",
                ArgumentDirection.InOut => "IN OUT",
                _ => "IN",
            }).Append(' ').Append(a.TypeName ?? a.DataType);
            if (a.HasDefault)
            {
                sb.Append(" := …");
            }
        }

        sb.Append(')');
        if (p.IsFunction && p.ReturnType is { } r)
        {
            sb.Append(" RETURN ").Append(r);
        }

        return sb.ToString();
    }

    private static string ColumnDetail(ColumnInfo c)
    {
        var sb = new StringBuilder(c.DataType);
        if (c.Precision is { } p)
        {
            sb.Append(CultureInfo.InvariantCulture, $"({p}{(c.Scale is { } s && s != 0 ? "," + s.ToString(CultureInfo.InvariantCulture) : string.Empty)})");
        }
        else if (c.Length is { } l && c.DataType.Contains("CHAR", StringComparison.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"({l})");
        }

        if (!c.IsNullable)
        {
            sb.Append(" NOT NULL");
        }

        if (!string.IsNullOrEmpty(c.Comment))
        {
            sb.Append(" — ").Append(c.Comment);
        }

        return sb.ToString();
    }

    // ---- 넣을 글자 ----------------------------------------------------------------------------

    /// <summary>사용자가 소문자로 쓰고 있으면 소문자로 넣는다(접두어, 없으면 커서 앞 가장 가까운 글자로 판단).</summary>
    private static bool PrefersLowerCase(string text, CompletionContext ctx)
    {
        foreach (var c in ctx.Prefix)
        {
            if (char.IsLetter(c))
            {
                return char.IsLower(c);
            }
        }

        for (int i = ctx.ReplaceRange.Start - 1, n = 0; i >= 0 && n < 200; i--, n++)
        {
            if (char.IsAsciiLetter(text[i]))
            {
                return char.IsLower(text[i]);
            }
        }

        return false;
    }

    private static string Format(string name, CompletionItemKind kind, bool lower)
    {
        if (kind is CompletionItemKind.Keyword or CompletionItemKind.Hint || (kind == CompletionItemKind.Function && name.All(IsPlainUpper)))
        {
            return lower ? name.ToLowerInvariant() : name;
        }

        if (name.Length > 0 && char.IsAsciiLetterUpper(name[0]) && name.All(IsPlainUpper))
        {
            return lower ? name.ToLowerInvariant() : name;
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsPlainUpper(char c) => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '$' or '#';
}
