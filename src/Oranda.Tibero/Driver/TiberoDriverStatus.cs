using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <inheritdoc cref="IDbmsDriverStatus"/>
/// <remarks>생성할 때 드라이버를 읽지 않는다. 경로는 부를 때마다 환경 변수·설정 순으로 다시 해석한다.</remarks>
[OrandaService(typeof(IDbmsDriverStatus), Dbms = Dbms.Tibero)]
internal sealed class TiberoDriverStatus : IDbmsDriverStatus
{
    private readonly IDriverPathSource? _paths;

    private readonly Func<string?> _environment;

    private readonly string _extractRoot;

    public TiberoDriverStatus(IDriverPathSource? paths = null)
        : this(paths, static () => Environment.GetEnvironmentVariable(TiberoDriverLocator.EnvironmentVariable), TiberoDriverLocator.DefaultExtractRoot)
    {
    }

    internal TiberoDriverStatus(IDriverPathSource? paths, Func<string?> environment, string extractRoot)
    {
        _paths = paths;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _extractRoot = extractRoot ?? throw new ArgumentNullException(nameof(extractRoot));
    }

    public Dbms Dbms => Dbms.Tibero;

    public string? ConfiguredPath
    {
        get
        {
            var path = _paths?.GetPath(Dbms.Tibero);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    public DriverCheck Current => Check(TiberoDriverLocator.SelectPath(_environment(), ConfiguredPath));

    // 드라이버를 올리는 DV3 에서 읽은 뒤 경로가 바뀌었는지로 바꾼다.
    public bool RestartRequired => false;

    public DriverCheck Check(string? path)
    {
        // 설정 화면 [확인] 의 글도 환경 변수·설정과 같게 다듬는다(따옴표·~·%VAR%).
        if (TiberoDriverLocator.SelectPath(null, path) is not { } normalized)
        {
            return DriverCheck.Missing;
        }

        var located = TiberoDriverLocator.Locate(normalized, _extractRoot);
        return located.DllPath is { } dll
            ? TiberoDriverLoader.Inspect(dll)
            : located.Failure!;
    }
}
