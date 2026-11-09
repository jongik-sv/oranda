using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <summary>
/// Tibero 드라이버 dll 의 메타데이터 사전 검사와 전용 로드 컨텍스트 적재. 사전 검사는 어셈블리를 올리지 않는다(PEReader 로 읽고 바로 닫는다).
/// </summary>
internal static class TiberoDriverLoader
{
    internal const string ExpectedName = "Tibero.DataAccess";

    /// <summary>시험해 확인한 버전(TDP.NET Core).</summary>
    internal static readonly Version VerifiedVersion = new(1, 1, 35);

    internal const string FrameworkBuildReason = ".NET Framework 용 TDP.NET(2.x)은 쓸 수 없습니다. TDP.NET Core 1.1.x 가 필요합니다.";

    private static readonly ConditionalWeakTable<AssemblyLoadContext, DriverFolder> Registered = new();

    private static readonly Lock Gate = new();

    /// <summary>파일을 잠그지 않고 읽어 검사한다.</summary>
    internal static DriverCheck Inspect(string dllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Inspect(stream, dllPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DriverCheck(DriverState.NotFound, null, dllPath, $"파일을 읽지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>PE 이미지의 어셈블리 정의·참조만 읽어 검사한다. 스트림은 열어 둔 채 돌려준다.</summary>
    internal static DriverCheck Inspect(Stream image, string reportedPath)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                return NotManaged(reportedPath);
            }

            if (pe.PEHeaders.CorHeader is not { } cor || (cor.Flags & CorFlags.ILOnly) == 0)
            {
                return NotManaged(reportedPath);
            }

            var md = pe.GetMetadataReader();
            if (!md.IsAssembly)
            {
                return NotManaged(reportedPath);
            }

            var definition = md.GetAssemblyDefinition();
            var name = md.GetString(definition.Name);
            if (!string.Equals(name, ExpectedName, StringComparison.Ordinal))
            {
                var reason = string.Equals(name, "Oracle.DataAccess", StringComparison.Ordinal)
                    ? "같은 패키지의 Oracle 호환 이름 변형이라 쓸 수 없습니다. Tibero.DataAccess.dll 을 지정하세요."
                    : $"Tibero 드라이버(Tibero.DataAccess)가 아닙니다(어셈블리 이름 '{name}').";
                return new DriverCheck(DriverState.WrongAssembly, null, reportedPath, reason);
            }

            var version = definition.Version;
            var versionText = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

            foreach (var handle in md.AssemblyReferences)
            {
                if (string.Equals(md.GetString(md.GetAssemblyReference(handle).Name), "mscorlib", StringComparison.Ordinal))
                {
                    return new DriverCheck(DriverState.FrameworkBuild, versionText, reportedPath, FrameworkBuildReason);
                }
            }

            if (version.Major != 1)
            {
                return new DriverCheck(DriverState.WrongAssembly, versionText, reportedPath,
                    $"지원하지 않는 버전입니다({versionText}). TDP.NET Core 1.1.x 가 필요합니다.");
            }

            if (version.Minor == VerifiedVersion.Minor && version.Build == VerifiedVersion.Build)
            {
                return new DriverCheck(DriverState.Ok, versionText, reportedPath);
            }

            return new DriverCheck(DriverState.Unverified, versionText, reportedPath,
                $"시험하지 않은 버전입니다({versionText}). {VerifiedVersion} 에서 확인했습니다.");
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return NotManaged(reportedPath);
        }
    }

    /// <summary>
    /// 드라이버를 <paramref name="context"/> 에 올린다. 드라이버가 직접 참조하는 어셈블리를 앱이 못 찾으면 드라이버 폴더에서 찾아 준다
    /// (이름이 참조 목록에 있고, 파일의 어셈블리 이름이 같고, 버전이 요청 이상일 때만). 한 컨텍스트에는 한 폴더의 드라이버만 올린다.
    /// </summary>
    internal static Assembly Load(AssemblyLoadContext context, string dllPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);

        var full = Path.GetFullPath(dllPath);
        var folder = Path.GetDirectoryName(full)!;

        lock (Gate)
        {
            if (Registered.TryGetValue(context, out var driverFolder))
            {
                if (!string.Equals(driverFolder.Path, folder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("이 로드 컨텍스트에는 이미 다른 폴더의 Tibero 드라이버를 올렸습니다. 다시 시작하면 적용됩니다.");
                }
            }
            else
            {
                driverFolder = new DriverFolder(folder);
                Registered.Add(context, driverFolder);
                context.Resolving += driverFolder.Resolve;
            }

            var assembly = context.LoadFromAssemblyPath(full);
            driverFolder.Allow(assembly);
            return assembly;
        }
    }

    /// <summary>드라이버 폴더에서 드라이버의 직접 참조만 찾아 주는 Resolving 처리기. 앱 전체의 다른 실패에는 끼어들지 않는다.</summary>
    private sealed class DriverFolder(string path)
    {
        private readonly Lock _gate = new();

        private HashSet<string> _references = new(StringComparer.OrdinalIgnoreCase);

        public string Path { get; } = path;

        public void Allow(Assembly driver)
        {
            var names = driver.GetReferencedAssemblies().Select(a => a.Name).OfType<string>();
            lock (_gate)
            {
                _references = new HashSet<string>(_references.Concat(names), StringComparer.OrdinalIgnoreCase);
            }
        }

        public Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
        {
            if (requested.Name is not { Length: > 0 } simple)
            {
                return null;
            }

            lock (_gate)
            {
                if (!_references.Contains(simple))
                {
                    return null;
                }
            }

            var candidate = System.IO.Path.Combine(Path, simple + ".dll");
            try
            {
                if (!File.Exists(candidate))
                {
                    return null;
                }

                var found = AssemblyName.GetAssemblyName(candidate);
                if (!string.Equals(found.Name, simple, StringComparison.OrdinalIgnoreCase)
                    || (requested.Version is { } wanted && (found.Version is null || found.Version < wanted)))
                {
                    return null;
                }

                return context.LoadFromAssemblyPath(candidate);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or FileLoadException)
            {
                return null; // 대신 찾지 못하면 원래의 「어셈블리 없음」 오류가 그대로 난다.
            }
        }
    }

    private static DriverCheck NotManaged(string path) =>
        new(DriverState.WrongAssembly, null, path, "관리 코드 어셈블리(.dll)가 아닙니다.");
}
