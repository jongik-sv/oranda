using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// 저장 필터(<see cref="SchemaBrowserFilter"/>, TSK-13-10)로 트리의 스키마·범주·객체를 거른다. 이름 패턴은 캐시에서 받은 목록에
/// 화면 쪽에서 건다(유형마다 패턴이 달라 한 번의 조회 조건으로 묶을 수 없다).
/// </summary>
public sealed class SavedFilterMatcher
{
    private readonly string? _logonUser;
    private readonly IReadOnlySet<string>? _schemasWithObjects;
    private readonly HashSet<string> _selected;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, Regex?> _patterns;

    public SavedFilterMatcher(SchemaBrowserFilter filter, string? logonUser, IReadOnlySet<string>? schemasWithObjects = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Filter = filter;
        _logonUser = logonUser;
        _schemasWithObjects = schemasWithObjects;
        _selected = new HashSet<string>(filter.Schemas, StringComparer.OrdinalIgnoreCase);
        _hidden = new HashSet<string>(filter.HiddenTypes, StringComparer.Ordinal);
        _patterns = filter.NamePatterns.ToDictionary(
            kv => kv.Key,
            kv => ToRegex(kv.Value, filter.UnderscoreIsWildcard, filter.WrapWithPercent),
            StringComparer.Ordinal);
    }

    public SchemaBrowserFilter Filter { get; }

    /// <summary>「객체를 가진 스키마만」 이라 객체 소유자 목록이 있어야 한다.</summary>
    public bool NeedsSchemasWithObjects => Filter.Scope == SchemaFilterScope.SchemasWithObjects;

    public bool ShowsSchema(string schema) => Filter.Scope switch
    {
        SchemaFilterScope.LogonSchema => string.Equals(schema, _logonUser, StringComparison.OrdinalIgnoreCase),
        SchemaFilterScope.SchemasWithObjects => _schemasWithObjects?.Contains(schema) ?? true,
        SchemaFilterScope.Selected => _selected.Contains(schema),
        _ => true,
    };

    public bool ShowsType(string objectType) => !_hidden.Contains(objectType);

    public bool Shows(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return ShowsType(obj.ObjectType)
            && (!_patterns.TryGetValue(obj.ObjectType, out var regex) || regex is null || regex.IsMatch(obj.Name));
    }

    /// <summary>
    /// 필터 패턴을 정규식으로 바꾼다. 비면 null(모두). <c>*</c>·<c>%</c> 는 여러 글자, <c>_</c> 는 옵션에 따라 한 글자 또는 글자 그대로.
    /// 와일드카드가 없고 <paramref name="wrapWithPercent"/> 면 포함 검색. 따옴표로 감싸면 대소문자 그대로, 아니면 대문자(Oracle 기본 이름).
    /// </summary>
    public static Regex? ToRegex(string? pattern, bool underscoreIsWildcard, bool wrapWithPercent)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var text = pattern.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }
        else
        {
            text = text.ToUpperInvariant();
        }

        var wildcard = text.Contains('*', StringComparison.Ordinal) || text.Contains('%', StringComparison.Ordinal);
        var sb = new StringBuilder("^");
        if (!wildcard && wrapWithPercent)
        {
            sb.Append("(?s:.*)");
        }

        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '*' or '%' => "(?s:.*)",
                '_' when underscoreIsWildcard => "(?s:.)",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        if (!wildcard && wrapWithPercent)
        {
            sb.Append("(?s:.*)");
        }

        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
