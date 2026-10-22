using System.Text.Json;
using Oranda.Core.Serialization;

namespace Oranda.Core.Connection;

/// <summary>
/// 접속 내보내기 파일 루트(TSK-13-13). <see cref="Format"/> 표시로 다른 JSON(SQL Developer·connections.json)과 구분한다.
/// 비밀번호·<c>credentialRef</c>·마지막 접속 시각은 담지 않는다.
/// </summary>
public sealed record ConnectionExportDocument
{
    /// <summary>파일 형식 표시.</summary>
    public const string FormatName = "oranda.connections";

    /// <summary>이 판이 쓰고 읽는 형식 판.</summary>
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;

    public int SchemaVersion { get; init; } = CurrentVersion;

    /// <summary>내보낸 시각(참고용, 읽을 때 쓰지 않는다).</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    public IReadOnlyList<ConnectionProfile> Profiles { get; init; } = [];
}

/// <summary>접속 내보내기 파일 쓰기·읽기와 「같은 접속」 판정.</summary>
public static class ConnectionExport
{
    /// <summary>파일에 넣지 않는 값(비밀 참조·이 PC 의 접속 기록)을 지운다.</summary>
    public static ConnectionProfile Strip(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { CredentialRef = null, LastConnectedAt = null };
    }

    /// <summary>고른 접속을 내보내기 파일 본문(JSON)으로.</summary>
    public static string Write(IEnumerable<ConnectionProfile> profiles, DateTimeOffset? exportedAt = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var doc = new ConnectionExportDocument { ExportedAt = exportedAt, Profiles = [.. profiles.Select(Strip)] };
        return JsonSerializer.Serialize(doc, OrandaJsonContext.Default.ConnectionExportDocument);
    }

    /// <summary>본문이 Oranda 내보내기 파일인지(루트 객체의 <c>format</c> 이 맞는지). JSON 이 아니면 false.</summary>
    public static bool LooksLikeExport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String
                && format.GetString() == ConnectionExportDocument.FormatName;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 내보내기 파일을 읽는다. 형식 표시가 없거나 더 새 판이면 <see cref="InvalidDataException"/>.
    /// 빠진 이름·사용자·접속 문자열은 빈 글자로 둔다(가져오기 화면이 검증해 표시한다). 비밀 참조·접속 기록은 지운다.
    /// </summary>
    public static IReadOnlyList<ConnectionProfile> Read(string json)
    {
        if (!LooksLikeExport(json))
        {
            throw new InvalidDataException("Oranda 접속 파일이 아니다(format 표시가 없다).");
        }

        ConnectionExportDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.ConnectionExportDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Oranda 접속 파일 형식이 틀렸다: " + ex.Message, ex);
        }

        if (doc is null)
        {
            throw new InvalidDataException("Oranda 접속 파일이 비어 있다.");
        }

        if (doc.SchemaVersion > ConnectionExportDocument.CurrentVersion)
        {
            throw new InvalidDataException($"더 새 판(schemaVersion {doc.SchemaVersion})의 Oranda 접속 파일이다. Oranda 를 업데이트하세요.");
        }

        return [.. (doc.Profiles ?? []).Where(p => p is not null).Select(p => Strip(p with
        {
            Name = p.Name ?? string.Empty,
            User = p.User ?? string.Empty,
            DataSource = p.DataSource ?? string.Empty,
        }))];
    }

    /// <summary>이름·사용자·접속 문자열이 같으면(앞뒤 공백·대소문자 무시) 같은 접속으로 본다.</summary>
    public static bool SameConnection(ConnectionProfile a, ConnectionProfile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Same(a.Name, b.Name) && Same(a.User, b.User) && Same(a.DataSource, b.DataSource);

        static bool Same(string? x, string? y) => string.Equals(x?.Trim(), y?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
