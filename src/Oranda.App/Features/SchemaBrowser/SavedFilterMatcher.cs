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
    private readonly Regex? _schemaRegex;
    private readonly string? _schemaPatternError;
    private bool _schemaPatternTimedOut;

    /// <summary>스키마 정규식 한 번 평가에 주는 시간(밀리초). 넘으면 그 스키마는 안 맞는 것으로 본다.</summary>
    public const int SchemaPatternTimeoutMs = 100;

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
        if (filter.Scope == SchemaFilterScope.Pattern)
        {
            _schemaRegex = TryParseSchemaPattern(filter.SchemaPattern, out _schemaPatternError);
        }
    }

    public SchemaBrowserFilter Filter { get; }

    /// <summary>
    /// 스키마 정규식을 쓸 수 없거나(잘못된 패턴) 평가가 시간 제한을 넘었을 때의 경고. 없으면 null.
    /// 설정 파일을 손으로 고친 경우에만 잘못된 패턴이 여기까지 온다(대화상자는 저장을 막는다).
    /// </summary>
    public string? SchemaPatternWarning =>
        _schemaPatternError is not null ? $"스키마 패턴이 올바르지 않아 스키마를 보이지 않는다: {_schemaPatternError}"
        : _schemaPatternTimedOut ? $"스키마 패턴 평가가 {SchemaPatternTimeoutMs} ms 를 넘겨 일부 스키마는 맞지 않는 것으로 처리했다"
        : null;

    /// <summary>「객체를 가진 스키마만」 이라 객체 소유자 목록이 있어야 한다.</summary>
    public bool NeedsSchemasWithObjects => Filter.Scope == SchemaFilterScope.SchemasWithObjects;

    public bool ShowsSchema(string schema) => Filter.Scope switch
    {
        SchemaFilterScope.LogonSchema => string.Equals(schema, _logonUser, StringComparison.OrdinalIgnoreCase),
        SchemaFilterScope.SchemasWithObjects => _schemasWithObjects?.Contains(schema) ?? true,
        SchemaFilterScope.Selected => _selected.Contains(schema),
        SchemaFilterScope.Pattern => MatchesSchemaPattern(schema),
        _ => true,
    };

    // 패턴이 비면 모두. 잘못된 패턴은 아무것도 안 맞는다(경고는 SchemaPatternWarning).
    private bool MatchesSchemaPattern(string schema)
    {
        if (_schemaPatternError is not null)
        {
            return false;
        }

        if (_schemaRegex is null)
        {
            return true;
        }

        try
        {
            return _schemaRegex.IsMatch(schema);
        }
        catch (RegexMatchTimeoutException)
        {
            _schemaPatternTimedOut = true;
            return false;
        }
    }

    /// <summary>
    /// 스키마 정규식을 만든다. 비면 null(모두 맞음, 오류 없음). 대소문자 무시·일부 일치(전체 일치는 <c>^…$</c>)에
    /// <see cref="SchemaPatternTimeoutMs"/> 시간 제한. 잘못된 패턴이면 null 과 이유를 돌려준다.
    /// </summary>
    public static Regex? TryParseSchemaPattern(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(
                pattern.Trim(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(SchemaPatternTimeoutMs));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

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
