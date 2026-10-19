using System.Text.Json;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>
/// SQL Developer 「접속 내보내기」 connections.json 가져오기(CONN-09). 소유: TSK-03-05.
/// 루트는 <c>{"connections":[{"name","type","info":{...}}]}</c>. 비밀번호(<c>password</c>)는 읽지 않고 버린다.
/// </summary>
[OrandaService(typeof(IConnectionImporter))]
internal sealed class SqlDeveloperImporter : IConnectionImporter
{
    public string SourceName => "SQL Developer";

    public IReadOnlyList<string> FileExtensions { get; } = [".json"];

    public Task<IReadOnlyList<ImportedConnection>> ImportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Parse(ImportHelpers.ReadText(path)));
    }

    /// <summary>본문을 해석한다. 형식이 틀리면 <see cref="InvalidDataException"/>.</summary>
    public static IReadOnlyList<ImportedConnection> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("SQL Developer connections.json 형식이 아니다: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement list = default;
            var found = root.ValueKind == JsonValueKind.Array
                || (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("connections", out list) && list.ValueKind == JsonValueKind.Array);
            if (!found)
            {
                throw new InvalidDataException("SQL Developer connections.json 형식이 아니다: connections 배열이 없다.");
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }

            var result = new List<ImportedConnection>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && ToConnection(item) is { } imported)
                {
                    result.Add(imported);
                }
            }

            return result;
        }
    }

    private static ImportedConnection? ToConnection(JsonElement item)
    {
        var info = item.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : item;
        var name = Text(item, "name") ?? Text(info, "ConnName");
        var user = Text(info, "user") ?? Text(info, "username");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var dataSource = ResolveDataSource(info);
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return null;
        }

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(user))
        {
            user = ImportHelpers.PlaceholderUser;
            warnings.Add("사용자 이름이 없어 자리 표시 이름을 넣었다.");
        }

        if (!string.IsNullOrEmpty(Text(info, "password")))
        {
            warnings.Add("비밀번호는 가져오지 않는다. 접속할 때 입력한다.");
        }

        var profile = new ConnectionProfile(
            name.Trim(),
            user.Trim(),
            dataSource,
            ImportHelpers.ParseRole(Text(info, "role")),
            TnsAdmin: NullIfBlank(Text(info, "TNSDirectory")),
            WalletLocation: NullIfBlank(Text(info, "walletLocation")));
        return new ImportedConnection(profile, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private static string? ResolveDataSource(JsonElement info)
    {
        var alias = Text(info, "networkAlias") ?? Text(info, "tnsAlias");
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return alias.Trim();
        }

        var host = Text(info, "hostname") ?? Text(info, "host");
        if (!string.IsNullOrWhiteSpace(host))
        {
            var service = Text(info, "serviceName");
            return ImportHelpers.EasyConnect(host, Text(info, "port"), string.IsNullOrWhiteSpace(service) ? Text(info, "sid") : service);
        }

        return ImportHelpers.FromJdbcUrl(Text(info, "customUrl") ?? Text(info, "url"));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Text(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }
}
