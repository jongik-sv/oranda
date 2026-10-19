using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>ADB 지갑 zip 을 살펴본 결과.</summary>
/// <param name="Aliases">tnsnames.ora 의 접속 별칭(파일에 적힌 순서).</param>
/// <param name="DefaultAlias">처음 고를 별칭(<c>_high</c> 우선, 없으면 첫째).</param>
/// <param name="HasAutoLoginWallet"><c>cwallet.sso</c>(자동 로그인 지갑)가 있다. 없으면 접속할 수 없다.</param>
/// <param name="SuggestedName">zip 파일 이름에서 뽑은 접속 이름(<c>Wallet_MYDB.zip</c> → <c>MYDB</c>).</param>
public sealed record WalletInfo(
    IReadOnlyList<string> Aliases,
    string? DefaultAlias,
    bool HasAutoLoginWallet,
    string SuggestedName);

/// <summary>지갑 zip 이 올바르지 않다.</summary>
public sealed class WalletFormatException : Exception
{
    public WalletFormatException()
    {
    }

    public WalletFormatException(string message)
        : base(message)
    {
    }

    public WalletFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// ADB 지갑 zip(CONN-03)을 읽고 풀어 놓는다. 자동 로그인 지갑(<c>cwallet.sso</c>)만 지원한다(data-model 5.1).
/// 필요한 파일만 폴더 이름 없이 풀고(zip-slip 방지), <c>sqlnet.ora</c> 의 지갑 위치를 풀어 놓은 폴더로 고쳐 쓴다.
/// </summary>
public static partial class WalletImporter
{
    /// <summary>풀어 놓을 파일(소문자). 나머지는 무시한다.</summary>
    private static readonly string[] Wanted =
    [
        "tnsnames.ora", "sqlnet.ora", "cwallet.sso", "ewallet.p12", "ewallet.pem", "keystore.jks", "truststore.jks", "ojdbc.properties",
    ];

    [GeneratedRegex(@"^([A-Za-z0-9_][A-Za-z0-9_.\-]*)\s*=", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AliasLine();

    [GeneratedRegex(@"DIRECTORY\s*=\s*""?[^"")\r\n]*""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WalletDirectory();

    /// <summary>zip 안을 읽어 별칭·자동 로그인 지갑 유무를 알려 준다. 파일은 만들지 않는다.</summary>
    public static WalletInfo Inspect(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = Index(zip);
            if (!entries.TryGetValue("tnsnames.ora", out var tns))
            {
                throw new WalletFormatException("지갑 zip 에 tnsnames.ora 가 없다. ADB 에서 받은 Wallet_*.zip 이 맞는지 확인하세요.");
            }

            using var reader = new StreamReader(tns.Open());
            var aliases = ReadAliases(reader.ReadToEnd());
            if (aliases.Count == 0)
            {
                throw new WalletFormatException("tnsnames.ora 에서 접속 별칭을 찾지 못했다.");
            }

            var preferred = aliases.FirstOrDefault(a => a.EndsWith("_high", StringComparison.OrdinalIgnoreCase)) ?? aliases[0];
            return new WalletInfo(aliases, preferred, entries.ContainsKey("cwallet.sso"), SuggestName(zipPath));
        }
        catch (InvalidDataException ex)
        {
            throw new WalletFormatException("zip 파일을 읽을 수 없다: " + ex.Message, ex);
        }
    }

    /// <summary>필요한 파일을 <paramref name="destination"/> 에 푼다(이미 있으면 덮어쓴다). 풀어 놓은 파일 이름 목록을 돌려준다.</summary>
    public static IReadOnlyList<string> Extract(string zipPath, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var root = Path.GetFullPath(destination);
        var written = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            Directory.CreateDirectory(root);
            foreach (var (name, entry) in Index(zip))
            {
                var target = Path.GetFullPath(Path.Combine(root, name));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name == "sqlnet.ora")
                {
                    using var reader = new StreamReader(entry.Open());
                    var text = reader.ReadToEnd();
                    File.WriteAllText(target, RewriteWalletDirectory(text, root));
                }
                else
                {
                    entry.ExtractToFile(target, overwrite: true);
                }

                written.Add(name);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new WalletFormatException("zip 파일을 읽을 수 없다: " + ex.Message, ex);
        }

        return written;
    }

    /// <summary>tnsnames.ora 본문에서 별칭 목록.</summary>
    public static IReadOnlyList<string> ReadAliases(string tnsnames)
    {
        ArgumentNullException.ThrowIfNull(tnsnames);
        var stripped = string.Join('\n', tnsnames.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        return [.. AliasLine().Matches(stripped).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>sqlnet.ora 의 <c>DIRECTORY=</c> 를 풀어 놓은 폴더로 바꾼다.</summary>
    public static string RewriteWalletDirectory(string sqlnet, string directory)
    {
        ArgumentNullException.ThrowIfNull(sqlnet);
        return WalletDirectory().Replace(sqlnet, m => $"DIRECTORY=\"{directory}\"");
    }

    private static string SuggestName(string zipPath)
    {
        var name = Path.GetFileNameWithoutExtension(zipPath);
        if (name.StartsWith("Wallet_", StringComparison.OrdinalIgnoreCase))
        {
            name = name["Wallet_".Length..];
        }

        return string.IsNullOrWhiteSpace(name) ? "ADB" : name;
    }

    private static Dictionary<string, ZipArchiveEntry> Index(ZipArchive zip)
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.Name.ToLowerInvariant();
            if (name.Length > 0 && Wanted.Contains(name, StringComparer.Ordinal))
            {
                map.TryAdd(name, entry);
            }
        }

        return map;
    }
}
