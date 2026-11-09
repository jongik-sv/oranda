using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

/// <summary>
/// 가짜 드라이버(출력 하위 폴더 fake-driver/)를 새 로드 컨텍스트에 한 번만 올려 묶은 어댑터와 시험 도우미.
/// 정적 처리기를 바꾸는 시험은 한 컬렉션에 두어 병렬로 돌리지 않는다.
/// </summary>
internal static class FakeDriverHost
{
    private static readonly Lazy<(TiberoDriver Driver, Assembly Assembly)> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static TiberoDriver Driver => Loaded.Value.Driver;

    internal static Assembly Assembly => Loaded.Value.Assembly;

    internal static string DllPath => Path.Combine(AppContext.BaseDirectory, "fake-driver", "Tibero.DataAccess.dll");

    /// <summary>
    /// 다음 명령 실행의 결과를 정하는 처리기(<see cref="DataTable"/>·int·값·null). null 이면 처리기를 뺀다.
    /// 처리기는 비동기 흐름마다 따로다(가짜의 AsyncLocal) — 시험 메서드 본문에서 바로 부를 것(async 도우미 안에서 부르면 호출한 쪽에 남지 않는다).
    /// </summary>
    internal static void SetHandler(Func<DbCommand, object?>? handler) =>
        Server().GetProperty("Handler")!.SetValue(null, handler);

    internal static Exception ServerError(int number, string message) =>
        (Exception)Server().GetMethod("ServerError")!.Invoke(null, [number, message])!;

    internal static Exception ClientError(int code, string message) =>
        (Exception)Server().GetMethod("ClientError")!.Invoke(null, [code, message])!;

    internal static void Reset() => Server().GetMethod("Reset")!.Invoke(null, null);

    /// <summary>가짜 드라이버 형식(전체 이름).</summary>
    internal static Type TypeOf(string fullName) =>
        Assembly.GetType(fullName, true)!;

    /// <summary>가짜 값을 만든다(예: <c>Tibero.DataAccess.Types.TiberoString</c>, 생성자 인자).</summary>
    internal static object New(string fullName, params object?[] args) =>
        Activator.CreateInstance(TypeOf(fullName), args)!;

    /// <summary>가짜 형식의 정적 필드 값(예: <c>TiberoString.Null</c>).</summary>
    internal static object Static(string fullName, string field) =>
        TypeOf(fullName).GetField(field)!.GetValue(null)!;

    /// <summary>
    /// 가짜 값 형식(<c>Tibero.DataAccess.Types</c> 의 짧은 이름, 예: <c>TiberoString</c>)의 값. 인자가 없으면 그 형식의 NULL(<c>IsNull</c> 인 <c>Null</c> 필드).
    /// 드라이버 이름공간 글자를 시험 파일마다 적지 않게 한다(adapter 문서 5장).
    /// </summary>
    internal static object ProviderValue(string typeName, params object?[] args) =>
        args.Length == 0 ? Static("Tibero.DataAccess.Types." + typeName, "Null") : New("Tibero.DataAccess.Types." + typeName, args);

    internal static object? Get(object target, string property) =>
        target.GetType().GetProperty(property)!.GetValue(target);

    /// <summary>같은 가짜 형식의 DataTable 기반 리더.</summary>
    internal static DbDataReader NewReader(System.Data.DataTable table) =>
        (DbDataReader)New("Tibero.DataAccess.Client.TiberoDataReader", table);

    private static Type Server() => TypeOf("Tibero.DataAccess.Client.FakeServer");

    private static (TiberoDriver, Assembly) Load()
    {
        var context = new AssemblyLoadContext("fake-tibero-driver", isCollectible: false);
        var assembly = TiberoDriverLoader.Load(context, DllPath);
        return (TiberoDriver.Bind(assembly), assembly);
    }
}

/// <summary>정적 처리기를 바꾸는 시험의 컬렉션 정의.</summary>
[CollectionDefinition("FakeDriver", DisableParallelization = true)]
public sealed class FakeDriverCollectionDefinition;
