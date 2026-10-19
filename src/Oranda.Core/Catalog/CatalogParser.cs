using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Catalog;

/// <summary>검사에서 걸린 문제 하나. <see cref="Rule"/> 은 <see cref="CatalogRules"/> 상수 중 하나.</summary>
public sealed record CatalogProblem(string Path, string Rule, string Message)
{
    public override string ToString() => $"{Path}: [{Rule}] {Message}";
}

/// <summary>검사 규칙 이름. docs/design/catalog.md 「검사 규칙」 표와 같다.</summary>
public static class CatalogRules
{
    public const string HeaderMissing = "header-missing";
    public const string HeaderInvalid = "header-invalid";
    public const string MinVersionTooLow = "min-version-too-low";
    public const string IdFolderMismatch = "id-folder-mismatch";
    public const string DuplicateId = "duplicate-id";
    public const string BodyEmpty = "body-empty";
}

/// <summary>
/// 머리 주석 파서. 파일 맨 앞의 연속된 <c>--</c> 줄이 머리 블록이고, 그 안의 <c>-- key: value</c> 줄이 필드다.
/// 값 뒤의 <c> -- 설명</c> 은 버린다. 키 형식이 아닌 <c>--</c> 줄은 자유 주석으로 본다.
/// </summary>
public static partial class CatalogParser
{
    public const string IdKey = "id";
    public const string MinVersionKey = "min-version";
    public const string RequiresKey = "requires";
    public const string LicenseKey = "license";
    public const string RacKey = "rac";

    private static readonly string[] KnownKeys = [IdKey, MinVersionKey, RequiresKey, LicenseKey, RacKey];

    [GeneratedRegex(@"^--\s*([a-z][a-z-]*)\s*:\s*(.*)$")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex IdFormat();

    /// <summary>원문 하나를 읽는다. 문제가 있으면 쿼리는 null 이고 문제 목록이 비어 있지 않다.</summary>
    public static (CatalogQuery? Query, IReadOnlyList<CatalogProblem> Problems) Parse(CatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<CatalogProblem>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var index = 0;
        for (; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (index == 0)
            {
                line = line.TrimStart('﻿');
            }

            if (!line.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var match = FieldLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value;
            var value = StripInlineComment(match.Groups[2].Value);
            if (!KnownKeys.Contains(key))
            {
                problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"모르는 키 '{key}'"));
            }
            else if (!fields.TryAdd(key, value))
            {
                problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"키 '{key}' 가 두 번 나온다"));
            }
        }

        var body = string.Join('\n', lines.Skip(index)).Trim();

        if (fields.Count == 0)
        {
            problems.Add(Problem(source, CatalogRules.HeaderMissing, "머리 주석(-- id: … 등)이 없다"));
            return (null, problems);
        }

        var id = Required(source, fields, IdKey, problems);
        if (id is not null && !IdFormat().IsMatch(id))
        {
            problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"id '{id}' 는 '폴더.이름' 형식의 소문자여야 한다"));
        }
        else if (id is not null && !string.Equals(id.Split('.')[0], TopFolder(source.Path), StringComparison.Ordinal))
        {
            problems.Add(Problem(source, CatalogRules.IdFolderMismatch,
                $"id '{id}' 의 첫 마디가 폴더 '{TopFolder(source.Path)}' 와 다르다"));
        }

        var minVersion = 0;
        var minVersionText = Required(source, fields, MinVersionKey, problems);
        if (minVersionText is not null)
        {
            if (!int.TryParse(minVersionText, NumberStyles.None, CultureInfo.InvariantCulture, out minVersion))
            {
                problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"min-version '{minVersionText}' 는 주 버전 정수여야 한다"));
            }
            else if (minVersion < CatalogHeader.LowestMinVersion)
            {
                problems.Add(Problem(source, CatalogRules.MinVersionTooLow,
                    $"min-version {minVersion} 은 지원 하한 {CatalogHeader.LowestMinVersion} 보다 낮다"));
            }
        }

        var license = CatalogLicense.None;
        var licenseText = Required(source, fields, LicenseKey, problems);
        if (licenseText is not null && !TryParseLicense(licenseText, out license))
        {
            problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"license '{licenseText}' 는 none|diagnostics|tuning 중 하나여야 한다"));
        }

        var rac = CatalogRac.V;
        if (fields.TryGetValue(RacKey, out var racText) && !TryParseRac(racText, out rac))
        {
            problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"rac '{racText}' 는 v|gv 중 하나여야 한다"));
        }

        var requires = fields.TryGetValue(RequiresKey, out var requiresText)
            ? requiresText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        if (body.Length == 0)
        {
            problems.Add(Problem(source, CatalogRules.BodyEmpty, "머리 주석 뒤에 SQL 본문이 없다"));
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        var header = new CatalogHeader(id!, minVersion, requires, license, rac, source.Path);
        return (new CatalogQuery(header, body), problems);
    }

    /// <summary>Catalog/ 아래 첫 폴더 이름. 폴더 없이 놓인 파일은 빈 문자열.</summary>
    public static string TopFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string? Required(CatalogSource source, Dictionary<string, string> fields, string key, List<CatalogProblem> problems)
    {
        if (fields.TryGetValue(key, out var value) && value.Length > 0)
        {
            return value;
        }

        problems.Add(Problem(source, CatalogRules.HeaderInvalid, $"필수 키 '{key}' 가 없다"));
        return null;
    }

    private static string StripInlineComment(string value)
    {
        var comment = value.IndexOf("--", StringComparison.Ordinal);
        return (comment < 0 ? value : value[..comment]).Trim();
    }

    private static bool TryParseLicense(string text, out CatalogLicense license)
    {
        (var ok, license) = text switch
        {
            "none" => (true, CatalogLicense.None),
            "diagnostics" => (true, CatalogLicense.Diagnostics),
            "tuning" => (true, CatalogLicense.Tuning),
            _ => (false, CatalogLicense.None),
        };
        return ok;
    }

    private static bool TryParseRac(string text, out CatalogRac rac)
    {
        (var ok, rac) = text switch
        {
            "v" => (true, CatalogRac.V),
            "gv" => (true, CatalogRac.Gv),
            _ => (false, CatalogRac.V),
        };
        return ok;
    }

    private static CatalogProblem Problem(CatalogSource source, string rule, string message) =>
        new(source.Path, rule, message);
}
