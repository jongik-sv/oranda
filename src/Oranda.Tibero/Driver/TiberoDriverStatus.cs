using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <inheritdoc cref="IDbmsDriverStatus"/>
/// <remarks>
/// 생성할 때 드라이버를 읽지 않는다. 드라이버를 아직 안 올렸으면 경로를 부를 때마다 환경 변수·설정 순으로 다시 해석해 검사하고,
/// 올렸으면 올린 것의 결과(묶기 실패 포함)를 준다.
/// </remarks>
[OrandaService(typeof(IDbmsDriverStatus), Dbms = Dbms.Tibero)]
internal sealed class TiberoDriverStatus : IDbmsDriverStatus
{
    private readonly IDriverPathSource? _paths;

    private readonly TiberoDriverHost _host;

    public TiberoDriverStatus(IDriverPathSource? paths = null)
        : this(paths, TiberoDriverHost.Shared)
    {
    }

    internal TiberoDriverStatus(IDriverPathSource? paths, TiberoDriverHost host)
    {
        _paths = paths;
        _host = host ?? throw new ArgumentNullException(nameof(host));
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

    public DriverCheck Current => _host.LoadedCheck ?? Check(_host.SelectPath(_paths));

    /// <summary>올린 뒤 고른 경로가 가리키는 dll 이 올린 것과 다르다(지우기·다른 파일). nupkg·폴더는 찾아낸 dll 로 비교한다.</summary>
    public bool RestartRequired
    {
        get
        {
            if (_host.LoadedPath is not { } loaded)
            {
                return false;
            }

            if (_host.SelectPath(_paths) is not { } selected)
            {
                return true;
            }

            var located = TiberoDriverLocator.Locate(selected, _host.ExtractRoot);
            return located.DllPath is not { } dll
                || !string.Equals(Path.GetFullPath(dll), loaded, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    public DriverCheck Check(string? path)
    {
        // 설정 화면 [확인] 의 글도 환경 변수·설정과 같게 다듬는다(따옴표·~·%VAR%).
        if (TiberoDriverLocator.SelectPath(null, path) is not { } normalized)
        {
            return DriverCheck.Missing;
        }

        var located = TiberoDriverLocator.Locate(normalized, _host.ExtractRoot);
        return located.DllPath is { } dll
            ? TiberoDriverLoader.Inspect(dll)
            : located.Failure!;
    }
}
