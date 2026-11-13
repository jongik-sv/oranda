using System.Globalization;
using System.Text;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Rollback.Backup;

/// <summary>
/// 백업 테이블 제안을 만들 때 공급자가 주는 값(되돌리기 지점 설계 9장, RD-17).
/// </summary>
/// <param name="CurrentSchema">문장에 스키마가 없을 때 대상 표의 소유자(세션의 현재 스키마)</param>
/// <param name="BackupSchema">백업 테이블을 만들 스키마(세션 사용자. 표 소유자 스키마에는 CREATE 권한이 없을 수 있다)</param>
/// <param name="MaxIdentifierBytes">서버의 식별자 한도(<see cref="IRollbackDialect.MaxIdentifierBytes"/>)</param>
/// <param name="LocalNow">이름의 <c>MMddHHmm</c> 를 만드는 지금 시각(PC 지역 시각)</param>
public sealed record BackupContext(string CurrentSchema, string BackupSchema, int MaxIdentifierBytes, DateTime LocalNow)
{
    /// <summary>대상 표의 PK 열(<see cref="DataLossKind.DropColumns"/> 일 때만 쓴다). 비면 ROWID 를 함께 담는다.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
}

/// <summary>
/// 데이터가 사라지는 DDL(TRUNCATE TABLE, ALTER TABLE … DROP COLUMN, DROP TABLE … PURGE) 앞의 백업 테이블(CTAS) 제안(설계 9장).
/// 순수 함수다. 대상인지는 분류기가 채운 <see cref="ChangeTarget.DataLoss"/> 로 알고, 확인 창이 나오는 접속에서만 부를지는 공급자가 정한다(RD-17).
/// <list type="bullet">
/// <item>기본 이름 <c>ORB_&lt;표 이름&gt;_&lt;MMddHHmm&gt;</c>. 서버 이름 한도(바이트)에 맞춰 표 이름 쪽을 자른다.</item>
/// <item>TRUNCATE·DROP … PURGE = <c>CREATE TABLE "백업 스키마"."이름" AS SELECT * FROM "소유자"."표"</c>.</item>
/// <item>DROP COLUMN = PK 열과 지울 열만 담는다. PK 가 없으면 <c>ROWIDTOCHAR(ROWID) AS ORB_ROWID</c> 를 앞에 더한다.</item>
/// </list>
/// 식별자는 늘 큰따옴표로 감싼다(대소문자·특수 문자 이름도 그대로 맞게).
/// </summary>
public static class BackupPlanner
{
    public const string NamePrefix = "ORB_";

    /// <summary>PK 없는 표의 DROP COLUMN 백업에 더하는 ROWID 열 이름.</summary>
    public const string RowIdColumn = "ORB_ROWID";

    private const string StampFormat = "MMddHHmm";

    /// <summary>제안을 만들려면 PK 열을 먼저 읽어야 하는가(DROP COLUMN 만).</summary>
    public static bool NeedsKeyColumns(ChangeTarget? target) => target?.DataLoss == DataLossKind.DropColumns;

    /// <summary>데이터가 사라지는 DDL 이면 제안, 아니면 null.</summary>
    public static BackupOffer? Offer(ChangeTarget? target, BackupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (target is null || target.DataLoss == DataLossKind.None || target.ObjectType is not ("TABLE" or null))
        {
            return null;
        }

        if (target.DataLoss == DataLossKind.DropColumns && target.DroppedColumns.Count == 0)
        {
            return null;
        }

        var owner = target.Owner ?? context.CurrentSchema;
        var table = new DbObjectRef(owner, target.Name, "TABLE");
        var source = SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(target.Name);
        var select = target.DataLoss == DataLossKind.DropColumns
            ? "SELECT " + string.Join(", ", DropColumnsList(target.DroppedColumns, context.KeyColumns)) + " FROM " + source
            : "SELECT * FROM " + source;
        var name = DefaultName(target.Name, context.MaxIdentifierBytes, context.LocalNow);
        return new BackupOffer(table, name, CreateSql(context.BackupSchema, name, select), ReasonOf(target.DataLoss));
    }

    /// <summary><c>ORB_&lt;표&gt;_&lt;MMddHHmm&gt;</c>. 전체가 <paramref name="maxBytes"/>(UTF-8 바이트) 안에 들게 표 이름을 글자 경계에서 자른다.</summary>
    public static string DefaultName(string table, int maxBytes, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(table);
        var suffix = "_" + localNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        var room = maxBytes - Encoding.UTF8.GetByteCount(NamePrefix + suffix);
        return NamePrefix + CutToBytes(table, Math.Max(room, 0)) + suffix;
    }

    /// <summary>CTAS 글. 백업 이름 칸을 고친 뒤 다시 만들 때도 쓴다(<see cref="Rename"/>).</summary>
    public static string CreateSql(string backupSchema, string backupName, string selectSql)
    {
        ArgumentNullException.ThrowIfNull(selectSql);
        return "CREATE TABLE " + SqlIdentifier.QuoteAlways(backupSchema) + "." + SqlIdentifier.QuoteAlways(backupName) + " AS " + selectSql;
    }

    /// <summary>
    /// 확인 창에서 이름을 고쳤을 때 제안을 다시 만든다. 따옴표로 감싼 이름은 그대로, 아닌 이름은 대문자로 본다(서버 규칙과 같다).
    /// 이름이 비었거나 제안의 글이 이 클래스가 만든 모양이 아니면 null.
    /// </summary>
    public static BackupOffer? Rename(BackupOffer offer, string newName)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var name = NormalizeName(newName);
        if (name is null || !TrySplit(offer.CreateSql, out var schema, out var select))
        {
            return null;
        }

        return offer with { BackupName = name, CreateSql = CreateSql(schema, name, select) };
    }

    /// <summary>
    /// <paramref name="createSql"/> 가 <paramref name="offer"/> 와 같은 백업인가: 같은 스키마·같은 SELECT 이고 이름만 다를 수 있다(<see cref="Rename"/> 결과 포함).
    /// 확인 창에서 돌아온 글이 정책 관문을 지나지 않은 다른 문장이 아닌지 공급자가 확인한다.
    /// </summary>
    public static bool IsSameBackup(BackupOffer offer, string createSql)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return createSql is not null
               && TrySplit(offer.CreateSql, out var schema, out var select)
               && TrySplit(createSql, out var otherSchema, out var otherSelect)
               && BackupNameOf(createSql) is not null
               && string.Equals(schema, otherSchema, StringComparison.Ordinal)
               && string.Equals(select, otherSelect, StringComparison.Ordinal);
    }

    /// <summary><see cref="CreateSql"/> 모양 글의 백업 표 이름(<c>스키마.이름</c>, 따옴표 없이). 모양이 아니면 null.</summary>
    public static string? BackupNameOf(string? createSql)
    {
        if (createSql is null || !TrySplit(createSql, out var schema, out _)
            || !TryReadQuoted(createSql, "CREATE TABLE ".Length + SqlIdentifier.QuoteAlways(schema).Length + 1, out var name, out _))
        {
            return null;
        }

        return schema + "." + name;
    }

    private static IEnumerable<string> DropColumnsList(IReadOnlyList<string> dropped, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            yield return "ROWIDTOCHAR(ROWID) AS " + RowIdColumn;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in keys.Concat(dropped))
        {
            if (seen.Add(column))
            {
                yield return SqlIdentifier.QuoteAlways(column); // PK 열을 지우는 경우(CASCADE CONSTRAINTS)에도 열이 두 번 나오지 않게(ORA-00957)
            }
        }
    }

    private static string ReasonOf(DataLossKind kind) => kind switch
    {
        DataLossKind.Truncate => "TRUNCATE 뒤에는 AS OF 로도 이전 데이터를 읽을 수 없습니다",
        DataLossKind.DropColumns => "DROP COLUMN 으로 지운 열의 값은 AS OF 로도 읽을 수 없습니다",
        _ => "DROP … PURGE 뒤에는 휴지통에도 남지 않아 표를 되살릴 수 없습니다",
    };

    private static string CutToBytes(string s, int maxBytes)
    {
        var bytes = 0;
        var i = 0;
        while (i < s.Length)
        {
            var width = char.IsSurrogatePair(s, i) ? 2 : 1;
            var add = Encoding.UTF8.GetByteCount(s.AsSpan(i, width));
            if (bytes + add > maxBytes)
            {
                break;
            }

            bytes += add;
            i += width;
        }

        return s[..i];
    }

    private static string? NormalizeName(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            var inner = t[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            return inner.Length == 0 ? null : inner;
        }

        return t.Contains('.', StringComparison.Ordinal) || t.Contains('"', StringComparison.Ordinal) ? null : t.ToUpperInvariant(); // 스키마는 바꿀 수 없다
    }

    /// <summary><see cref="CreateSql"/> 가 만든 글을 백업 스키마와 SELECT 로 나눈다.</summary>
    private static bool TrySplit(string createSql, out string schema, out string select)
    {
        schema = select = string.Empty;
        const string head = "CREATE TABLE ";
        if (!createSql.StartsWith(head, StringComparison.Ordinal)
            || !TryReadQuoted(createSql, head.Length, out schema, out var afterSchema)
            || afterSchema >= createSql.Length || createSql[afterSchema] != '.'
            || !TryReadQuoted(createSql, afterSchema + 1, out _, out var afterName))
        {
            return false;
        }

        const string asWord = " AS ";
        if (string.CompareOrdinal(createSql, afterName, asWord, 0, asWord.Length) != 0)
        {
            return false;
        }

        select = createSql[(afterName + asWord.Length)..];
        return true;
    }

    private static bool TryReadQuoted(string s, int start, out string value, out int next)
    {
        value = string.Empty;
        next = start;
        if (start >= s.Length || s[start] != '"')
        {
            return false;
        }

        var sb = new StringBuilder();
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                value = sb.ToString();
                next = i + 1;
                return true;
            }

            sb.Append(s[i]);
            i++;
        }

        return false;
    }
}
