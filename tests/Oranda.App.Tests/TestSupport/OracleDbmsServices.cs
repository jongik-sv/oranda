using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema;

namespace Oranda.App.Tests.TestSupport;

/// <summary>
/// 시험용 DBMS 별 구현 고르기. 실제 Oracle 구현(<c>Oranda.Oracle</c> 의 <c>[OrandaService(…, Dbms = Oracle)]</c> internal 클래스)을
/// 특성으로 찾아 <see cref="DbmsServiceTable"/> 에 넣는다 — 앱 조립이 하는 일을 시험에서 필요한 서비스만 골라 흉내 낸다.
/// App 화면이 정적 도우미 대신 Core 인터페이스(<see cref="ISchemaStatements"/>·<see cref="IDdlBulkExporter"/>·<see cref="IColumnReorderScripts"/>)를
/// 부르므로, 기존 Oracle 동작 시험은 이 선택기를 꽂아 통과시킨다.
/// </summary>
internal static class OracleDbmsServices
{
    private static readonly Type[] SchemaServices = [typeof(ISchemaStatements), typeof(IDdlBulkExporter), typeof(IColumnReorderScripts)];

    /// <summary>Oracle 용 <see cref="ISchemaStatements"/>·<see cref="IDdlBulkExporter"/>·<see cref="IColumnReorderScripts"/> 가 든 선택기(시험마다 새로 만든다).</summary>
    public static DbmsServiceTable CreateSelector()
    {
        var table = new DbmsServiceTable();
        var assembly = typeof(Oranda.Oracle.Schema.SchemaStatements).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(OrandaServiceAttribute), inherit: false).Cast<OrandaServiceAttribute>())
            {
                if (attribute.ForDbms == Dbms.Oracle && SchemaServices.Contains(attribute.ServiceType))
                {
                    var implementation = type;
                    table.Add(attribute.ServiceType, Dbms.Oracle, () => Activator.CreateInstance(implementation, nonPublic: true)!);
                }
            }
        }

        return table;
    }

    /// <summary>서비스 공급자로 쓰는 선택기. 시험의 <c>Services(params object[])</c> 에 항목으로 넣는다.</summary>
    public static DbmsServiceTable Selector { get; } = CreateSelector();

    /// <summary>Oracle 용 <see cref="ISchemaStatements"/> (정적 <c>SchemaStatements</c> 와 같은 결과).</summary>
    public static ISchemaStatements Statements => Selector.Get<ISchemaStatements>(Dbms.Oracle);

    /// <summary>Oracle 용 <see cref="IColumnReorderScripts"/>.</summary>
    public static IColumnReorderScripts ColumnReorderScripts => Selector.Get<IColumnReorderScripts>(Dbms.Oracle);

    /// <summary>선택기 하나만 든 서비스 공급자.</summary>
    public static IServiceProvider Provider { get; } = new SelectorProvider(Selector);

    private sealed class SelectorProvider(IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDbmsServiceSelector) ? selector : null;
    }
}
