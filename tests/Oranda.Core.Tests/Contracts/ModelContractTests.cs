using System.Reflection;
using Oranda.Core.Connection;
using Oranda.Core.Formatting;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Workspaces;

namespace Oranda.Core.Tests.Contracts;

/// <summary>data-model.md 6장 C# 매핑(이름·순서·기본값)과 「비밀 없음」 시험 ①.</summary>
public class ModelContractTests
{
    /// <summary>파일·DB 에 저장되는 record(data-model 6.1). 루트에서 닿는 하위 record 는 재귀로 훑는다.</summary>
    private static readonly Type[] PersistedRoots =
    [
        typeof(AppSettings), typeof(ConnectionsDocument), typeof(FormatterProfile), typeof(Workspace),
        typeof(HistoryEntry), typeof(MetaSource), typeof(MetaObject),
    ];

    private static readonly string[] SecretWords = ["password", "pwd", "secret", "token"];

    [Fact]
    public void Connection_profile_keeps_positional_order_and_appends_new_parameters()
    {
        var ctor = typeof(ConnectionProfile).GetConstructors().Single(c => c.GetParameters().Length > 1);

        Assert.Equal(
            ["Name", "User", "DataSource", "Auth", "TnsAdmin", "WalletLocation", "ConnectTimeoutSeconds",
             "Id", "Environment", "ReadOnly", "LicensePack", "KeepAliveSeconds", "Nls", "CredentialRef",
             "ProxyUser", "Color", "Group", "LastConnectedAt", "Comment"],
            ctor.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public void Connection_profile_defaults_follow_data_model()
    {
        var p = new ConnectionProfile("n", "u", "ds");

        Assert.Equal(AuthMode.Default, p.Auth);
        Assert.Equal(15, p.ConnectTimeoutSeconds);
        Assert.Equal(Guid.Empty, p.Id);
        Assert.Equal(ConnectionEnvironment.Development, p.Environment);
        Assert.False(p.ReadOnly);
        Assert.Equal(LicensePack.None, p.LicensePack);
        Assert.Equal(0, p.KeepAliveSeconds);
        Assert.Null(p.CredentialRef);
        Assert.Null(p.Comment);
    }

    [Fact]
    public void Enums_have_data_model_members_in_order()
    {
        Assert.Equal(["Default", "SysDba", "SysOper", "SysBackup", "SysDg", "SysKm"], Enum.GetNames<AuthMode>());
        Assert.Equal(["Development", "Test", "Production"], Enum.GetNames<ConnectionEnvironment>());
        Assert.Equal(["None", "Diagnostics", "DiagnosticsAndTuning"], Enum.GetNames<LicensePack>());
        Assert.Equal(["Query", "Dml", "Ddl", "PlSql", "TransactionControl", "SqlPlus", "Other"], Enum.GetNames<Oranda.Core.Execution.StatementKind>());
        Assert.Equal(["Editor", "Script", "GridEdit", "SchemaBrowser", "Monitor", "Extension"], Enum.GetNames<HistorySource>());
        Assert.Equal(["Succeeded", "Failed", "Cancelled", "Denied"], Enum.GetNames<HistoryStatus>());
        Assert.Equal(Enum.GetNames<HistoryStatus>(), Enum.GetNames<Oranda.Core.Execution.ExecutionStatus>());
        Assert.Equal(["Ask", "Stop", "Continue"], Enum.GetNames<Oranda.Core.Execution.ScriptErrorMode>());
    }

    [Fact]
    public void Formatter_option_defaults_are_oranda_default_profile()
    {
        var o = new FormatterOptions();

        Assert.Equal(LetterCase.Upper, o.Case.Keyword);
        Assert.Equal(LetterCase.AsIs, o.Case.Identifier);
        Assert.Equal(4, o.Indent.Size);
        Assert.Equal(CommaPosition.Trailing, o.CommaPosition);
        Assert.Equal(AndOrBreak.Before, o.LineBreaks.AndOr);
        Assert.Equal(120, o.LineBreaks.MaxLineWidth);
        Assert.False(o.Align.EqualsSign);
        Assert.Equal(StatementTerminator.Keep, o.StatementTerminator);
        Assert.True(o.HonorFormatterOff);
    }

    [Fact]
    public void App_settings_defaults_follow_data_model()
    {
        var s = new AppSettings();

        Assert.Equal(1, s.SchemaVersion);
        Assert.Equal(Theme.System, s.Ui.Theme);
        Assert.Equal("oranda-default", s.FormatterProfileId);
        Assert.False(s.Execution.AutoCommit);
        Assert.False(s.History.SaveBindValues);
        Assert.Equal(500, s.Grid.PageSize);
        Assert.Equal(10, s.Workspace.AutosaveSeconds);
    }

    [Fact]
    public void Persisted_models_have_no_secret_like_property_names()
    {
        var violations = new List<string>();
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>(PersistedRoots);

        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SecretWords.Any(w => property.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{type.Name}.{property.Name}");
                }

                foreach (var next in Expand(property.PropertyType))
                {
                    pending.Push(next);
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        var candidates = type.IsGenericType ? type.GetGenericArguments().Append(type) : [type];
        return candidates.Where(t => t.Namespace?.StartsWith("Oranda.Core", StringComparison.Ordinal) == true && !t.IsEnum);
    }
}
