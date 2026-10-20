using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

public enum SchemaNodeKind
{
    Favorites,
    Schema,
    Category,
    Item,
    Folder,
    Column,
    Index,
    Constraint,
    More,
    Placeholder,
    Message,
}

/// <summary>표·뷰 노드 아래 폴더 종류(TSK-13-08). 선언 순서가 화면 순서다.</summary>
public enum SchemaFolderKind
{
    Columns,
    Indexes,
    Constraints,
    Triggers,
    SnapshotLog,
    RecycleBin,
}

/// <summary>편집기에 넣을 내용 3종(SCH-07).</summary>
public enum SchemaInsertMode
{
    Name,
    Select,
    Columns,
}

/// <summary>트리 노드. 펼치면 소유 ViewModel 이 자식을 지연 로딩한다.</summary>
public sealed partial class SchemaNode : ObservableObject
{
    private readonly SchemaBrowserViewModel? _owner;
    private bool _isExpanded;

    internal SchemaNode(SchemaBrowserViewModel? owner, SchemaNodeKind kind, string title)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
    }

    public SchemaNodeKind Kind { get; }

    [ObservableProperty]
    private string _title;

    public DbObjectRef? Ref { get; internal init; }

    public string? Owner { get; internal init; }

    /// <summary>폴더 노드의 종류. 다른 노드는 null.</summary>
    public SchemaFolderKind? FolderKind { get; internal init; }

    /// <summary>표·뷰 노드: 폴더들이 같이 쓰는 <c>GetTableAsync</c> 결과(Columns·Indexes·Constraints 가 한 번만 읽는다).</summary>
    internal Task<TableInfo>? TableLoad { get; set; }

    public string? Status { get; internal set; }

    /// <summary>인덱스·제약 노드: 누르면 여는 표(<see cref="Ref"/> 는 비워 즐겨찾기·속성 창이 따라가지 않게 한다).</summary>
    internal DbObjectRef? OpenRef { get; init; }

    /// <summary>누르면 열 대상. 객체 노드는 자기 자신, 인덱스·제약은 소속 표, 그 밖은 null.</summary>
    internal DbObjectRef? OpenTarget => Kind == SchemaNodeKind.Item ? Ref : Kind is SchemaNodeKind.Index or SchemaNodeKind.Constraint ? OpenRef : null;

    /// <summary>열 노드의 자료형 요약 등 부가 설명.</summary>
    public string? Detail { get; internal init; }

    /// <summary>범주 노드: 아직 화면에 올리지 않은 전체 항목.</summary>
    internal IReadOnlyList<DbObjectRef> Items { get; set; } = [];

    internal int Shown { get; set; }

    internal SchemaNode? Parent { get; init; }

    internal bool Loaded { get; set; }

    internal bool Loading { get; set; }

    [ObservableProperty]
    private bool _isFavorite;

    public ObservableCollection<SchemaNode> Children { get; } = [];

    public bool IsInvalid => Status is not null && !string.Equals(Status, "VALID", StringComparison.Ordinal);

    /// <summary>범주 노드가 묶은 객체 유형(<c>TABLE</c> 등). 다른 노드는 null.</summary>
    public string? CategoryType { get; internal init; }

    /// <summary>트리 앞 아이콘 키(<see cref="Oranda.App.Shell.Icons.ShellIcons"/>). Orange 처럼 객체 유형마다 다르게 보인다.</summary>
    public string? IconKey => Kind switch
    {
        SchemaNodeKind.Favorites => "star",
        SchemaNodeKind.Schema => "schemaUser",
        SchemaNodeKind.Category => IconForType(CategoryType),
        SchemaNodeKind.Item => IconForType(Ref?.ObjectType),
        SchemaNodeKind.Column => "column",
        SchemaNodeKind.Index => "objIndex",
        SchemaNodeKind.Constraint => "key",
        SchemaNodeKind.Folder => FolderKind switch
        {
            SchemaFolderKind.Columns => "column",
            SchemaFolderKind.Indexes => "objIndex",
            SchemaFolderKind.Constraints => "key",
            SchemaFolderKind.Triggers => "objTrigger",
            SchemaFolderKind.SnapshotLog => "objMview",
            SchemaFolderKind.RecycleBin => "recycle",
            _ => null,
        },
        _ => null,
    };

    internal static string IconForType(string? type) => type switch
    {
        "TABLE" => "table",
        "VIEW" => "objView",
        "MATERIALIZED VIEW" => "objMview",
        "INDEX" => "objIndex",
        "PACKAGE" or "PACKAGE BODY" => "objPackage",
        "PROCEDURE" => "objProcedure",
        "FUNCTION" => "objFunction",
        "TYPE" or "TYPE BODY" => "objType",
        "SEQUENCE" => "objSequence",
        "TRIGGER" => "objTrigger",
        "SYNONYM" => "objSynonym",
        "DATABASE LINK" => "objDbLink",
        "JOB" or "SCHEDULER JOB" => "objJob",
        "JAVA CLASS" or "JAVA SOURCE" => "objJava",
        _ => "objOther",
    };

    public bool HasChildrenToLoad => Kind is SchemaNodeKind.Favorites or SchemaNodeKind.Schema or SchemaNodeKind.Category or SchemaNodeKind.Folder
        || (Kind == SchemaNodeKind.Item && Ref is { } r && IsTableLike(r.ObjectType));

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && _owner is not null)
            {
                _ = _owner.ExpandAsync(this);
            }
        }
    }

    /// <summary>펼침 표시만 바꾼다(자식은 이미 읽은 상태일 때, 다시 읽지 않게).</summary>
    internal bool IsExpandedSilently
    {
        set
        {
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    internal static bool IsTableLike(string type) => type is "TABLE" or "VIEW" or "MATERIALIZED VIEW";

    internal void ResetChildren()
    {
        Children.Clear();
        Loaded = false;
        Items = [];
        Shown = 0;
        TableLoad = null;
        if (HasChildrenToLoad)
        {
            Children.Add(new SchemaNode(null, SchemaNodeKind.Placeholder, "…"));
        }
    }
}

/// <summary>필터 바 값. 이름 패턴 · 유형 · INVALID 만 · 즐겨찾기만.</summary>
public sealed record SchemaFilter(string? Text = null, string? ObjectType = null, bool InvalidOnly = false, bool FavoritesOnly = false)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && ObjectType is null && !InvalidOnly && !FavoritesOnly;

    /// <summary>
    /// 사용자 입력을 SQL LIKE(이스케이프 <c>\</c>)로 바꾼다. <c>*</c>·<c>%</c> 는 와일드카드, 그 밖의 <c>_</c> 는 글자 그대로.
    /// 와일드카드가 없으면 「포함」 검색. 따옴표로 감싸면 대소문자를 그대로, 아니면 대문자로(Oracle 기본 이름).
    /// </summary>
    public static string? ToLikePattern(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var keepCase = text.Length >= 2 && text[0] == '"' && text[^1] == '"';
        if (keepCase)
        {
            text = text[1..^1];
        }
        else
        {
            text = text.ToUpperInvariant();
        }

        var hasWildcard = text.Contains('*', StringComparison.Ordinal) || text.Contains('%', StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '*':
                case '%':
                    sb.Append('%');
                    break;
                case '_':
                case '\\':
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return hasWildcard ? sb.ToString() : $"%{sb}%";
    }
}

/// <summary>접속별 즐겨찾기. 키는 <see cref="SchemaSql.FavoriteKey"/>. 영구 저장 구현은 저장소 계약이 생기면 붙인다.</summary>
public interface ISchemaFavoriteStore
{
    Task<IReadOnlySet<string>> LoadAsync(Guid profileId, CancellationToken ct);

    Task SetAsync(Guid profileId, string key, bool isFavorite, CancellationToken ct);
}

public sealed class InMemorySchemaFavoriteStore : ISchemaFavoriteStore
{
    private readonly Dictionary<Guid, HashSet<string>> _data = [];
    private readonly object _gate = new();

    public Task<IReadOnlySet<string>> LoadAsync(Guid profileId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlySet<string> copy = _data.TryGetValue(profileId, out var s)
                ? new HashSet<string>(s, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return Task.FromResult(copy);
        }
    }

    public Task SetAsync(Guid profileId, string key, bool isFavorite, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(profileId, out var s))
            {
                _data[profileId] = s = new HashSet<string>(StringComparer.Ordinal);
            }

            _ = isFavorite ? s.Add(key) : s.Remove(key);
        }

        return Task.CompletedTask;
    }
}

/// <summary>편집기에 넣을 SQL 조각 만들기(순수 함수).</summary>
public static partial class SchemaSql
{
    /// <summary>드래그 데이터 형식 이름(편집기가 DbObjectRef 를 알아볼 때 쓴다). 본문은 <see cref="FavoriteKey"/> 형식.</summary>
    public const string DragFormat = "application/x-oranda-schema-object";

    public static string FavoriteKey(DbObjectRef r) => $"{r.Owner}.{r.Name}|{r.ObjectType}";

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex PlainIdentifier();

    public static string Quote(string identifier) =>
        PlainIdentifier().IsMatch(identifier) ? identifier : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string QualifiedName(DbObjectRef r) => $"{Quote(r.Owner)}.{Quote(r.Name)}";

    public static string ColumnList(IEnumerable<ColumnInfo> columns, string separator = ", ") =>
        string.Join(separator, columns.Where(c => !c.IsInvisible).OrderBy(c => c.Position).Select(c => Quote(c.Name)));

    public static string Select(DbObjectRef r, IReadOnlyList<ColumnInfo>? columns)
    {
        if (r.ObjectType == "SEQUENCE")
        {
            return $"SELECT {QualifiedName(r)}.NEXTVAL FROM DUAL";
        }

        if (!SchemaNode.IsTableLike(r.ObjectType) || columns is null || columns.Count == 0)
        {
            return QualifiedName(r);
        }

        var list = ColumnList(columns, ",\n       ");
        return $"SELECT {list}\nFROM   {QualifiedName(r)}";
    }
}
