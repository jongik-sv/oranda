using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// 포매터 프로필(기본 제공 3종 + <c>formatter/{id}.json</c> 사용자 프로필) 저장·내보내기·가져오기(data-model 2.3).
/// 읽기는 <see cref="JsonDocumentFile"/>(손상 백업·미래 버전 읽기 전용), 쓰기는 원자적. 옵션 값은 data-model 범위로 맞춘다.
/// 가져오기는 기존 프로필을 덮어쓰지 않는다: ID·이름이 겹치면 새 ID·이름을 붙여 저장한다. 소유: TSK-05-03.
/// </summary>
[OrandaService(typeof(IFormatterProfileStore))]
internal sealed partial class FormatterProfileStore : IFormatterProfileStore
{
    public const string DefaultId = "oranda-default";

    public const string Orange5Id = "orange5";

    public const string CompactId = "compact";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IAppDataPaths _paths;
    private readonly IEnumerable<ISettingsMigration> _migrations;
    private readonly HashSet<string> _readOnlyIds = new(StringComparer.Ordinal);

    public FormatterProfileStore(IAppDataPaths paths, IEnumerable<ISettingsMigration> migrations)
    {
        _paths = paths;
        _migrations = migrations;
    }

    /// <summary>기본 제공 3종(지울 수도 고칠 수도 없다. 복사해서 쓴다).</summary>
    public static IReadOnlyList<FormatterProfile> BuiltIns { get; } =
    [
        new FormatterProfile { Id = DefaultId, Name = "Oranda 기본", Options = FormatterOptions.OrandaDefault },
        new FormatterProfile
        {
            Id = Orange5Id,
            Name = "Orange 5 스타일",
            Options = new FormatterOptions
            {
                Case = new CaseOptions { Keyword = LetterCase.Upper, Function = LetterCase.Lower, Identifier = LetterCase.Lower },
                Indent = new IndentOptions { Size = 2 },
                CommaPosition = CommaPosition.Leading,
                LineBreaks = new LineBreakOptions { AndOr = AndOrBreak.Before, InListThreshold = 3, MaxLineWidth = 100 },
                Align = new AlignOptions { Alias = true, Assignment = true, ColumnDefinition = true, EqualsSign = true },
            },
        },
        new FormatterProfile
        {
            Id = CompactId,
            Name = "간결",
            Options = new FormatterOptions
            {
                Indent = new IndentOptions { Size = 2 },
                OperatorSpacing = true,
                LineBreaks = new LineBreakOptions { SelectList = SelectListLayout.FillToWidth, JoinClause = false, AndOr = AndOrBreak.None, InListThreshold = 0, MaxLineWidth = 100 },
                Align = new AlignOptions { Alias = false, Assignment = false, ColumnDefinition = false, EqualsSign = false },
                Comments = new CommentOptions { Preserve = true, MaxBlankLines = 0 },
            },
        },
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z")]
    private static partial Regex IdPattern();

    public static bool IsBuiltIn(string id) => BuiltIns.Any(p => p.Id == id);

    public async Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ListCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(FormatterProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = ValidateId(profile.Id);
        if (IsBuiltIn(id))
        {
            throw new InvalidOperationException($"기본 제공 프로필({id})은 고칠 수 없다. 복사해서 쓴다.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("프로필 이름이 비었다.", nameof(profile));
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ListCoreAsync(ct).ConfigureAwait(false);
            if (_readOnlyIds.Contains(id))
            {
                throw new InvalidOperationException($"새 버전 Oranda 가 만든 프로필({id})이라 덮어쓰지 않는다.");
            }

            if (existing.Any(p => p.Id != id && string.Equals(p.Name, profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"같은 이름의 프로필이 이미 있다: {profile.Name}");
            }

            await WriteAsync(Normalize(profile, id), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        id = ValidateId(id);
        if (IsBuiltIn(id))
        {
            throw new InvalidOperationException($"기본 제공 프로필({id})은 지울 수 없다.");
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            File.Delete(PathOf(id));
            File.Delete(PathOf(id) + ".tmp");
            _readOnlyIds.Remove(id);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ExportAsync(string id, string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ListCoreAsync(ct).ConfigureAwait(false);
            var profile = all.FirstOrDefault(p => p.Id == id) ?? throw new FileNotFoundException($"프로필을 찾을 수 없다: {id}");
            await JsonDocumentFile.WriteAsync(path, profile with { SchemaVersion = SchemaVersions.Formatter }, OrandaJsonContext.Default.FormatterProfile, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<FormatterProfile> ImportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        FormatterProfile imported;
        try
        {
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject obj
                || obj["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var rawId) || string.IsNullOrWhiteSpace(rawId)
                || obj["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var rawName) || string.IsNullOrWhiteSpace(rawName))
            {
                throw new InvalidDataException("포매터 프로필 파일이 아니다(id·name 이 없다).");
            }

            var merged = JsonDocumentFile.OverDefaults(node, new FormatterProfile { Id = rawId, Name = rawName }, OrandaJsonContext.Default.FormatterProfile);
            imported = merged.Deserialize(OrandaJsonContext.Default.FormatterProfile) ?? throw new InvalidDataException("프로필을 읽을 수 없다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("포매터 프로필 파일 형식이 올바르지 않다: " + ex.Message, ex);
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ListCoreAsync(ct).ConfigureAwait(false);
            var ids = existing.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var names = existing.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var baseId = IdPattern().IsMatch(imported.Id) ? imported.Id : Slug(imported.Id);
            var id = baseId;
            for (var n = 2; ids.Contains(id); n++)
            {
                var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
                id = (baseId.Length + suffix.Length > 64 ? baseId[..(64 - suffix.Length)] : baseId) + suffix;
            }

            var name = imported.Name.Trim();
            var baseName = name;
            for (var n = 2; names.Contains(name); n++)
            {
                name = $"{baseName} ({n.ToString(CultureInfo.InvariantCulture)})";
            }

            var saved = Normalize(imported with { Name = name }, id);
            await WriteAsync(saved, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>data-model 2.3 에 적힌 범위로 맞춘다.</summary>
    internal static FormatterProfile Normalize(FormatterProfile p, string id)
    {
        var o = p.Options ?? new FormatterOptions();
        var indent = o.Indent ?? new IndentOptions();
        var lb = o.LineBreaks ?? new LineBreakOptions();
        var comments = o.Comments ?? new CommentOptions();
        return p with
        {
            Id = id,
            Name = p.Name.Trim(),
            SchemaVersion = SchemaVersions.Formatter,
            Options = o with
            {
                Case = o.Case ?? new CaseOptions(),
                Align = o.Align ?? new AlignOptions(),
                Indent = indent with
                {
                    Size = Math.Clamp(indent.Size, 1, 16),
                    Subquery = Math.Clamp(indent.Subquery, 0, 4),
                    Case = Math.Clamp(indent.Case, 0, 4),
                    PlsqlBlock = Math.Clamp(indent.PlsqlBlock, 0, 4),
                },
                LineBreaks = lb with
                {
                    InListThreshold = Math.Max(0, lb.InListThreshold),
                    MaxLineWidth = Math.Clamp(lb.MaxLineWidth, 40, 400),
                },
                Comments = comments with { MaxBlankLines = Math.Clamp(comments.MaxBlankLines, 0, 5) },
            },
        };
    }

    private static string Slug(string text)
    {
        var s = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length == 0)
        {
            s = "imported";
        }

        return s.Length > 64 ? s[..64].TrimEnd('-') : s;
    }

    private static string ValidateId(string id)
    {
        if (id is null || !IdPattern().IsMatch(id))
        {
            throw new ArgumentException($"프로필 ID 형식이 아니다: '{id}' (^[a-z0-9][a-z0-9-]{{0,63}}$)", nameof(id));
        }

        return id;
    }

    private string PathOf(string id) => Path.Combine(_paths.FormatterDirectory, id + ".json");

    private Task WriteAsync(FormatterProfile profile, CancellationToken ct)
        => JsonDocumentFile.WriteAsync(PathOf(profile.Id), profile, OrandaJsonContext.Default.FormatterProfile, ct);

    private async Task<IReadOnlyList<FormatterProfile>> ListCoreAsync(CancellationToken ct)
    {
        var list = new List<FormatterProfile>(BuiltIns);
        if (!Directory.Exists(_paths.FormatterDirectory))
        {
            return list;
        }

        var users = new List<FormatterProfile>();
        foreach (var file in Directory.EnumerateFiles(_paths.FormatterDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IdPattern().IsMatch(id) || IsBuiltIn(id))
            {
                continue;
            }

            var result = await JsonDocumentFile.LoadAsync(
                file,
                SettingsFileKinds.Formatter,
                SchemaVersions.Formatter,
                OrandaJsonContext.Default.FormatterProfile,
                () => new FormatterProfile { Id = id, Name = id },
                _migrations,
                _paths.BackupDirectory,
                ct).ConfigureAwait(false);

            // 없거나 손상(백업으로 옮김)·변환 실패는 목록에서 뺀다.
            if (result.State is JsonLoadState.Missing or JsonLoadState.Corrupt or JsonLoadState.MigrationFailed)
            {
                continue;
            }

            if (result.IsReadOnly)
            {
                _readOnlyIds.Add(id);
            }

            users.Add(Normalize(result.Value, id));
        }

        list.AddRange(users.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
        return list;
    }
}
