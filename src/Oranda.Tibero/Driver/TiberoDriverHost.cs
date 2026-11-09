using System.Runtime.Loader;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <summary>
/// 드라이버를 한 번만 읽어 묶는 곳. 앱은 <see cref="Shared"/>(기본 로드 컨텍스트, 실제 환경 변수) 하나를 쓰고, 시험은 새 로드 컨텍스트로 따로 만든다.
/// 읽기 전 실패(지정 안 함·파일 없음·검사 거절·올리기 실패)는 기억하지 않아 사용자가 경로를 고치면 다음 호출이 다시 해석한다.
/// 올린 뒤 묶기에 실패하면(멤버 없음·드라이버 정적 초기화 실패 등) 그 실패를 계속 돌려준다 — 같은 컨텍스트에 다시 올릴 수 없다.
/// </summary>
internal sealed class TiberoDriverHost
{
    private readonly Lock _gate = new();

    private readonly AssemblyLoadContext _context;

    private readonly Func<string?> _environment;

    private TiberoDriver? _driver;

    private TiberoDriverBindException? _bindFailure;

    private string? _loadedPath;

    private DriverCheck? _loadedCheck;

    internal TiberoDriverHost(AssemblyLoadContext context, Func<string?> environment, string extractRoot)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ExtractRoot = extractRoot ?? throw new ArgumentNullException(nameof(extractRoot));
    }

    /// <summary>앱이 쓰는 하나: 기본 로드 컨텍스트, 환경 변수 <c>ORANDA_TIBERO_DRIVER</c>, 기본 nupkg 풀기 폴더.</summary>
    internal static TiberoDriverHost Shared { get; } = new(
        AssemblyLoadContext.Default,
        static () => Environment.GetEnvironmentVariable(TiberoDriverLocator.EnvironmentVariable),
        TiberoDriverLocator.DefaultExtractRoot);

    /// <summary>nupkg 를 푸는 폴더.</summary>
    internal string ExtractRoot { get; }

    /// <summary>읽어 묶은 어댑터. 아직 없으면 null.</summary>
    internal TiberoDriver? Loaded
    {
        get
        {
            lock (_gate)
            {
                return _driver;
            }
        }
    }

    /// <summary>올린 dll 의 전체 경로(nupkg·폴더를 지정했으면 찾아낸 dll). 올리기 전이면 null.</summary>
    internal string? LoadedPath
    {
        get
        {
            lock (_gate)
            {
                return _loadedPath;
            }
        }
    }

    /// <summary>올린 것의 검사 결과(묶기에 실패했으면 그 결과). 올리기 전이면 null.</summary>
    internal DriverCheck? LoadedCheck
    {
        get
        {
            lock (_gate)
            {
                return _loadedCheck;
            }
        }
    }

    /// <summary>환경 변수 → 설정 순으로 고른 경로(다듬은 것). 둘 다 없으면 null.</summary>
    internal string? SelectPath(IDriverPathSource? paths) =>
        TiberoDriverLocator.SelectPath(_environment(), paths?.GetPath(Dbms.Tibero));

    /// <summary>어댑터를 돌려준다. 못 쓰면 <see cref="TiberoDriverUnavailableException"/>.</summary>
    internal TiberoDriver Require(IDriverPathSource? paths)
    {
        lock (_gate)
        {
            if (_driver is not null)
            {
                return _driver;
            }

            if (_bindFailure is not null)
            {
                throw _bindFailure;
            }

            if (SelectPath(paths) is not { } selected)
            {
                throw new TiberoDriverUnavailableException(DriverCheck.Missing);
            }

            var located = TiberoDriverLocator.Locate(selected, ExtractRoot);
            if (located.DllPath is not { } dll)
            {
                throw new TiberoDriverUnavailableException(located.Failure ?? DriverCheck.Missing);
            }

            var check = TiberoDriverLoader.Inspect(dll);
            if (!check.IsUsable)
            {
                throw new TiberoDriverUnavailableException(check);
            }

            System.Reflection.Assembly assembly;
            try
            {
                assembly = TiberoDriverLoader.Load(_context, dll);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new TiberoDriverUnavailableException(
                    new DriverCheck(DriverState.WrongAssembly, check.Version, check.Path, $"드라이버를 올리지 못했습니다: {ex.Message}"));
            }

            // 여기부터는 어셈블리가 컨텍스트에 올라 있다 — 실패하면 다시 올릴 수 없으므로 기억한다.
            _loadedPath = Path.GetFullPath(dll);
            try
            {
                _driver = TiberoDriver.Bind(assembly);
                _loadedCheck = check;
                return _driver;
            }
            catch (TiberoDriverBindException ex)
            {
                _bindFailure = ex;
                _loadedCheck = ex.Check;
                throw;
            }
        }
    }
}
