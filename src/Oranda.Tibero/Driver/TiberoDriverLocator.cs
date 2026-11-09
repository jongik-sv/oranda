using System.IO.Compression;
using System.Security.Cryptography;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <summary><see cref="TiberoDriverLocator.Locate"/> 의 결과: 찾은 dll 경로이거나 실패한 <see cref="DriverCheck"/>.</summary>
internal readonly record struct LocateResult(string? DllPath, DriverCheck? Failure);

/// <summary>
/// 사용자가 지정한 경로(dll·폴더·nupkg)에서 Tibero.DataAccess.dll 을 찾는다. 환경 변수가 설정보다 앞선다.
/// nupkg 는 추출 폴더에 풀어 둔다(파일 내용 해시로 폴더를 나눠 재사용).
/// </summary>
internal static class TiberoDriverLocator
{
    internal const string EnvironmentVariable = "ORANDA_TIBERO_DRIVER";

    internal const string AssemblyFileName = "Tibero.DataAccess.dll";

    private const string NupkgEntry = "lib/net5.0/" + AssemblyFileName;

    /// <summary>nupkg 안 dll 크기 상한(실제 1.1.35 는 약 2MB). 넘으면 풀지 않는다.</summary>
    private const long MaxEntryBytes = 64L * 1024 * 1024;

    private static readonly Lock ExtractGate = new();

    /// <summary>nupkg 를 푸는 기본 폴더. 사용자 데이터 폴더가 없는 환경(HOME 없는 CI 등)은 임시 폴더.</summary>
    internal static string DefaultExtractRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } data ? data : Path.GetTempPath(),
        "Oranda", "drivers", "tibero");

    /// <summary>
    /// 환경 변수 값이 비어 있지 않으면 그것, 아니면 설정 값, 둘 다 비면 null. 앞뒤 공백·따옴표(Windows 「경로로 복사」)를 걷고
    /// 환경 변수(<c>%USERPROFILE%</c>)와 앞의 <c>~</c> 를 펼친다.
    /// </summary>
    internal static string? SelectPath(string? environmentValue, string? configuredPath)
    {
        var raw = !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue : configuredPath;
        return Normalize(raw);
    }

    private static string? Normalize(string? raw)
    {
        var path = raw?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty);
        }

        return path;
    }

    /// <summary><paramref name="path"/> 에서 dll 을 찾는다. nupkg 는 <paramref name="extractRoot"/> 아래에 푼다.</summary>
    internal static LocateResult Locate(string path, string extractRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractRoot);

        if (Directory.Exists(path))
        {
            var direct = Path.Combine(path, AssemblyFileName);
            if (File.Exists(direct))
            {
                return Found(direct);
            }

            var nested = Path.Combine(path, "lib", "net5.0", AssemblyFileName);
            if (File.Exists(nested))
            {
                return Found(nested);
            }

            return Fail(DriverState.NotFound, path, $"폴더 '{path}' 에서 {AssemblyFileName} 을 찾지 못했습니다.");
        }

        if (!File.Exists(path))
        {
            return Fail(DriverState.NotFound, path, $"'{path}' 에 파일이 없습니다.");
        }

        if (path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractNupkg(path, extractRoot);
        }

        return Found(path);
    }

    private static LocateResult ExtractNupkg(string nupkg, string extractRoot)
    {
        try
        {
            // 해시와 푸는 사이에 다른 프로그램이 고쳐 쓰지 못하게 읽기 공유만 연다.
            using var file = new FileStream(nupkg, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(file));
            file.Position = 0;

            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), NupkgEntry, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return Fail(DriverState.WrongAssembly, nupkg, $"nupkg 에 {NupkgEntry} 이 없습니다. TDP.NET Core 패키지가 맞는지 확인하세요.");
            }

            if (entry.Length > MaxEntryBytes)
            {
                return Fail(DriverState.WrongAssembly, nupkg, $"nupkg 의 {AssemblyFileName} 이 너무 큽니다({entry.Length:N0}바이트).");
            }

            var folder = Path.Combine(extractRoot, hash);
            var target = Path.Combine(folder, AssemblyFileName);

            // 두 확인(설정 [확인]·Current)이 동시에 풀지 않게 한다. 이미 올린(잠긴) 파일을 덮어쓰지 않는다 — 해시 폴더라 내용이 같다.
            lock (ExtractGate)
            {
                if (File.Exists(target) && new FileInfo(target).Length == entry.Length)
                {
                    return Found(target);
                }

                Directory.CreateDirectory(folder);
                var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var source = entry.Open())
                    using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyAtMost(source, destination, entry.Length);
                    }

                    File.Move(temp, target, overwrite: true);
                }
                catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == entry.Length)
                {
                    // 다른 프로세스가 먼저 풀어 두었고 그 파일이 쓰이는 중 — 같은 내용이므로 그것을 쓴다.
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }

            return Found(target);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return Fail(DriverState.WrongAssembly, nupkg, "nupkg 파일이 손상되었거나 읽을 수 없는 압축 형식입니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(DriverState.NotFound, nupkg, $"nupkg 를 읽거나 풀지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>항목 머리의 크기보다 많이 나오면 멈춘다(압축 폭탄·손상 방지).</summary>
    private static void CopyAtMost(Stream source, Stream destination, long expected)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > expected)
            {
                throw new InvalidDataException("압축 항목이 머리에 적힌 크기보다 큽니다.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static LocateResult Found(string dll) => new(dll, null);

    private static LocateResult Fail(DriverState state, string path, string reason) =>
        new(null, new DriverCheck(state, null, path, reason));
}
