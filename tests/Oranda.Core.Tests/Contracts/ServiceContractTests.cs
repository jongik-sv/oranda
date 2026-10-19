using System.Reflection;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Formatting;
using Oranda.Core.Grid;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.Core.Tests.Contracts;

/// <summary>
/// TRD 6장·4.10 서비스 인터페이스의 존재와 시그니처(이름·반환 형식·매개변수 형식)를 고정한다.
/// 시그니처를 바꾸려면 docs/design/design.md 와 이 표를 함께 고친다.
/// </summary>
public class ServiceContractTests
{
    private static readonly Type Ct = typeof(CancellationToken);

    public static TheoryData<Type, string, Type, Type[]> Methods => new()
    {
        { typeof(ISessionManager), "OpenAsync", typeof(Task<ISession>), [typeof(ConnectionProfile), typeof(SecureCredential), typeof(SessionPurpose), Ct] },
        { typeof(ISession), "CommitAsync", typeof(Task), [Ct] },
        { typeof(ISession), "RollbackAsync", typeof(Task), [Ct] },
        { typeof(IExecutionService), "ExecuteAsync", typeof(Task<ExecutionResult>), [typeof(ISession), typeof(SqlStatement), typeof(IReadOnlyDictionary<string, BindValue>), typeof(ExecutionOptions), Ct] },
        { typeof(IExecutionService), "ExecuteScriptAsync", typeof(IAsyncEnumerable<ExecutionResult>), [typeof(ISession), typeof(IReadOnlyList<SqlStatement>), typeof(ScriptErrorMode), typeof(ExecutionOptions), Ct] },
        { typeof(IExecutionPolicy), "EvaluateAsync", typeof(ValueTask<PolicyDecision>), [typeof(PolicyContext), typeof(SqlStatement), Ct] },
        { typeof(IResultSet), "GetPageAsync", typeof(Task<ResultPage>), [typeof(int), Ct] },
        { typeof(IRowEditor), "Analyze", typeof(EditCapability), [typeof(SqlStatement), typeof(IReadOnlyList<ResultColumn>), typeof(TableKeyInfo)] },
        { typeof(IRowEditor), "BuildDml", typeof(IReadOnlyList<SqlStatement>), [typeof(IReadOnlyList<RowChange>)] },
        { typeof(IRowEditor), "ApplyAsync", typeof(Task<ApplyResult>), [typeof(ISession), typeof(IReadOnlyList<RowChange>), typeof(bool), Ct] },
        { typeof(IMetadataProvider), "ListObjectsAsync", typeof(Task<IReadOnlyList<DbObjectRef>>), [typeof(ISession), typeof(ObjectQuery), Ct] },
        { typeof(IMetadataProvider), "GetTableAsync", typeof(Task<TableInfo>), [typeof(ISession), typeof(DbObjectRef), Ct] },
        { typeof(IMetadataProvider), "GetDdlAsync", typeof(Task<string>), [typeof(ISession), typeof(DbObjectRef), typeof(DdlOptions), Ct] },
        { typeof(IMonitorService), "GetSessionsAsync", typeof(Task<IReadOnlyList<SessionRow>>), [typeof(ISession), typeof(SessionFilter), Ct] },
        { typeof(IMonitorService), "GetBlockingTreeAsync", typeof(Task<BlockingTree>), [typeof(ISession), Ct] },
        { typeof(IMonitorService), "GetTopSqlAsync", typeof(Task<IReadOnlyList<TopSqlRow>>), [typeof(ISession), typeof(TopSqlCriteria), Ct] },
        { typeof(IMonitorService), "KillAsync", typeof(Task), [typeof(ISession), typeof(SessionKey), typeof(bool), Ct] },
        { typeof(ILanguageService), "Split", typeof(IReadOnlyList<SqlStatement>), [typeof(string), typeof(SplitOptions)] },
        { typeof(ILanguageService), "GetStatementAt", typeof(SqlStatement), [typeof(string), typeof(int), typeof(SplitOptions)] },
        { typeof(ILanguageService), "AnalyzeAsync", typeof(Task<IReadOnlyList<Diagnostic>>), [typeof(string), Ct] },
        { typeof(ILanguageService), "GetCompletionContext", typeof(CompletionContext), [typeof(string), typeof(int)] },
        { typeof(ISqlFormatter), "Format", typeof(FormatResult), [typeof(string), typeof(FormatterOptions), typeof(TextRange?)] },
        { typeof(ICredentialStore), "SaveAsync", typeof(Task), [typeof(string), typeof(ReadOnlyMemory<byte>)] },
        { typeof(ICredentialStore), "LoadAsync", typeof(Task<byte[]>), [typeof(string)] },
        { typeof(ICredentialStore), "DeleteAsync", typeof(Task), [typeof(string)] },
        { typeof(IEditorContext), "ProposeEditAsync", typeof(Task<bool>), [typeof(TextEdit), Ct] },
        { typeof(IOutboundConsent), "RequestAsync", typeof(Task<ConsentDecision>), [typeof(OutboundRequest), Ct] },
    };

    [Theory]
    [MemberData(nameof(Methods))]
    public void Interface_method_has_contract_signature(Type contract, string name, Type returns, Type[] parameters)
    {
        Assert.True(contract.IsInterface, $"{contract.Name} 는 인터페이스여야 한다");
        Assert.Equal("Oranda.Core", contract.Assembly.GetName().Name);

        var method = contract.GetMethod(name, parameters);

        Assert.True(method is not null, $"{contract.Name}.{name}({string.Join(", ", parameters.Select(p => p.Name))}) 가 없다");
        Assert.Equal(returns, method!.ReturnType);
    }

    [Theory]
    [InlineData(typeof(ISession), "Id", typeof(Guid))]
    [InlineData(typeof(ISession), "Profile", typeof(ConnectionProfile))]
    [InlineData(typeof(ISession), "Server", typeof(ServerInfo))]
    [InlineData(typeof(ISession), "State", typeof(SessionState))]
    [InlineData(typeof(ISession), "HasPendingTransaction", typeof(bool))]
    [InlineData(typeof(ISessionManager), "Sessions", typeof(IReadOnlyList<ISession>))]
    [InlineData(typeof(IResultSet), "Columns", typeof(IReadOnlyList<ResultColumn>))]
    [InlineData(typeof(IResultSet), "IsComplete", typeof(bool))]
    [InlineData(typeof(IResultSet), "KnownRowCount", typeof(long?))]
    public void Interface_property_has_contract_type(Type contract, string name, Type type)
    {
        var property = contract.GetProperty(name);

        Assert.True(property is not null, $"{contract.Name}.{name} 가 없다");
        Assert.Equal(type, property!.PropertyType);
    }

    [Theory]
    [InlineData(typeof(ISession))]
    [InlineData(typeof(IResultSet))]
    public void Session_and_result_set_are_async_disposable(Type contract)
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(contract));
    }

    [Fact]
    public void Every_public_interface_in_core_is_documented_by_namespace()
    {
        // 공개 인터페이스는 영역 네임스페이스(Oranda.Core.<영역>) 아래에만 둔다.
        var stray = typeof(ISession).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface && (t.Namespace is null || t.Namespace.Count(c => c == '.') < 2))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(stray);
    }

    [Fact]
    public void Async_contract_methods_take_cancellation_token_last()
    {
        var violations = typeof(ISession).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => (t, m)))
            .Where(x => x.m.GetParameters().Any(p => p.ParameterType == Ct)
                        && x.m.GetParameters()[^1].ParameterType != Ct)
            .Select(x => $"{x.t.Name}.{x.m.Name}")
            .ToList();

        Assert.Empty(violations);
    }
}
