using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>저장하려는 <c>connections.json</c> 내용이 규칙(data-model 2.2)을 어겼다.</summary>
public sealed class ConnectionValidationException : Exception
{
    public ConnectionValidationException()
    {
        Errors = [];
    }

    public ConnectionValidationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public ConnectionValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public ConnectionValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>접속 프로필 한 개의 규칙 검사(data-model 2.2 「검증」). 접속 관리자 화면도 같은 검사를 쓴다.</summary>
public static partial class ConnectionProfileValidator
{
    /// <summary><c>password=</c>·<c>pwd=</c> 가 들어 있는 연결 문자열.</summary>
    [GeneratedRegex(@"(?:^|[\s;,(&?])(?:password|pwd)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordKeyword();

    /// <summary><c>user/secret@host</c> 형태(SQL*Plus 식).</summary>
    [GeneratedRegex(@"^\s*[^\s/@:()]+\s*/\s*[^@\s][^@]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserSlashSecret();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>접속 문자열에 비밀이 들어 있으면 true.</summary>
    public static bool ContainsSecret(string? dataSource) =>
        !string.IsNullOrEmpty(dataSource) && (PasswordKeyword().IsMatch(dataSource) || UserSlashSecret().IsMatch(dataSource));

    /// <summary>프로필 하나의 문제 목록. 비었으면 정상.</summary>
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        var label = string.IsNullOrWhiteSpace(profile.Name) ? "(이름 없음)" : profile.Name;

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("접속 이름이 비어 있다.");
        }

        if (string.IsNullOrWhiteSpace(profile.User))
        {
            errors.Add($"'{label}': 사용자가 비어 있다.");
        }

        if (string.IsNullOrWhiteSpace(profile.DataSource))
        {
            errors.Add($"'{label}': 접속 문자열이 비어 있다.");
        }
        else if (ContainsSecret(profile.DataSource))
        {
            errors.Add($"'{label}': 접속 문자열에 비밀번호를 넣을 수 없다. 비밀번호는 안전 저장소에만 저장한다.");
        }

        if (!string.IsNullOrEmpty(profile.User) && profile.User.Contains('/', StringComparison.Ordinal))
        {
            errors.Add($"'{label}': 사용자에 '/' 를 쓸 수 없다(비밀번호가 섞인 것으로 본다).");
        }

        if (profile.Color is { Length: > 0 } color && !HexColor().IsMatch(color))
        {
            errors.Add($"'{label}': 색은 #RRGGBB 형식이어야 한다.");
        }

        return errors;
    }
}

/// <summary>connections.json 읽기·쓰기·검증(data-model 2.2). 소유: TSK-03-04.</summary>
[OrandaService(typeof(IConnectionStore))]
internal sealed class ConnectionStore : IConnectionStore, IDisposable
{
    private readonly IAppDataPaths _paths;
    private readonly IEnumerable<ISettingsMigration> _migrations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _readOnly;

    public ConnectionStore(IAppDataPaths paths, IEnumerable<ISettingsMigration> migrations)
    {
        _paths = paths;
        _migrations = migrations;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ReadAsync(ct).ConfigureAwait(false);
            _readOnly = result.IsReadOnly;

            var profiles = result.Value.Profiles.Select(NormalizeProfile).ToList();
            var invalid = profiles.Where(p => ConnectionProfileValidator.Validate(p).Count > 0).Select(p => p.Name).ToList();
            var notice = result.Notice;
            if (invalid.Count > 0)
            {
                var text = $"규칙에 맞지 않는 접속이 있다(접속 문자열에 비밀번호 등): {string.Join(", ", invalid)}";
                notice = notice is null ? text : notice + Environment.NewLine + text;
            }

            return new StoredDocument<ConnectionsDocument>(result.Value with { Profiles = profiles, Folders = ConnectionFolders.Distinct(result.Value.Folders) }, result.IsReadOnly) { Notice = notice };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ConnectionsDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        var profiles = document.Profiles
            .Select(NormalizeProfile)
            .Select(p => p.Id == Guid.Empty ? p with { Id = Guid.NewGuid() } : p)
            .ToList();
        Validate(profiles);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly is null)
            {
                _readOnly = (await ReadAsync(ct).ConfigureAwait(false)).IsReadOnly;
            }

            if (_readOnly == true)
            {
                return;
            }

            var normalized = new ConnectionsDocument { SchemaVersion = SchemaVersions.Connections, Profiles = profiles, Folders = ConnectionFolders.Distinct(document.Folders) };
            await JsonDocumentFile.WriteAsync(_paths.ConnectionsFile, normalized, OrandaJsonContext.Default.ConnectionsDocument, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>keepAliveSeconds 1~59 → 60 (CONN-12). 음수는 0(끔). 그룹(폴더 경로)은 단계마다 공백을 다듬는다.</summary>
    internal static ConnectionProfile NormalizeProfile(ConnectionProfile p) => AiAccessPolicy.Clamp(p with
    {
        Group = ConnectionFolders.Normalize(p.Group),
        KeepAliveSeconds = p.KeepAliveSeconds switch
        {
            <= 0 => 0,
            < 60 => 60,
            _ => p.KeepAliveSeconds,
        },
    });

    private static void Validate(IReadOnlyList<ConnectionProfile> profiles)
    {
        var errors = new List<string>();
        foreach (var p in profiles)
        {
            errors.AddRange(ConnectionProfileValidator.Validate(p));
        }

        foreach (var dup in profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            errors.Add($"접속 이름 '{dup.Key}' 이(가) 중복된다.");
        }

        foreach (var dup in profiles.GroupBy(p => p.Id).Where(g => g.Key != Guid.Empty && g.Count() > 1))
        {
            errors.Add($"접속 식별자 {dup.Key} 이(가) 중복된다.");
        }

        if (errors.Count > 0)
        {
            throw new ConnectionValidationException(errors);
        }
    }

    private Task<JsonLoadResult<ConnectionsDocument>> ReadAsync(CancellationToken ct) =>
        JsonDocumentFile.LoadAsync(
            _paths.ConnectionsFile,
            SettingsFileKinds.Connections,
            SchemaVersions.Connections,
            OrandaJsonContext.Default.ConnectionsDocument,
            () => new ConnectionsDocument(),
            _migrations,
            _paths.BackupDirectory,
            ct);
}
