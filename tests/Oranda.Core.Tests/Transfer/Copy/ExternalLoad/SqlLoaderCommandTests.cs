using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. SQL*Loader 실행 정보 — 비밀번호는 표준 입력이나 현재 사용자만 읽는 parfile 에만 있다(조정 결정 89).
public sealed class SqlLoaderCommandTests : IDisposable
{
    private const string Password = "Pw#Secret@9!";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-sqlldr-cmd-" + Guid.NewGuid().ToString("N"));

    public SqlLoaderCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SqlLoaderRunSpec Spec(LoaderPasswordVia via, string connect = "dbhost:1521/ORCLPDB1", string? tnsAdmin = null) => new(
        @"C:\ic\sqlldr.exe",
        _dir,
        Path.Combine(_dir, "load.par"),
        Path.Combine(_dir, "load.ctl"),
        Path.Combine(_dir, "load.log"),
        Path.Combine(_dir, "load.bad"),
        new ExternalLoadLogin("SCOTT", connect, Password, tnsAdmin),
        MaxErrors: 50,
        Direct: true,
        via)
    {
        Timeout = TimeSpan.FromHours(1),
    };

    private static void AssertNoPassword(SqlLoaderInvocation invocation)
    {
        Assert.DoesNotContain(Password, invocation.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, invocation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, invocation.LoggableParFileText, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, invocation.Start.ToString(), StringComparison.Ordinal);
        Assert.All(invocation.Start.Arguments, a => Assert.DoesNotContain(Password, a, StringComparison.Ordinal));
        Assert.All(invocation.Start.Environment.Values, v => Assert.DoesNotContain(Password, v ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Standard_input_mode_keeps_the_password_out_of_arguments_parfile_and_descriptions()
    {
        var invocation = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.StandardInput));

        AssertNoPassword(invocation);
        Assert.Equal(new[] { "parfile=" + Path.Combine(_dir, "load.par") }, invocation.Start.Arguments);
        // userid 전체를 「Username:」 답으로(비밀번호만 빼면 EZConnect 의 '/' 를 비밀번호 구분자로 읽는다, #181).
        Assert.Equal("SCOTT/\"" + Password + "\"@dbhost:1521/ORCLPDB1\n", invocation.Start.StandardInput);
        Assert.Equal(_dir, invocation.Start.WorkingDirectory);
        Assert.Equal(TimeSpan.FromHours(1), invocation.Start.Timeout);

        var lines = invocation.LoggableParFileText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        Assert.DoesNotContain(lines, l => l.StartsWith("userid=", StringComparison.Ordinal));
        Assert.Contains("(표준 입력) SCOTT/\"" + SqlLoaderCommand.PasswordMask + "\"@dbhost:1521/ORCLPDB1", invocation.Describe(), StringComparison.Ordinal);
        Assert.Contains("control=" + Path.Combine(_dir, "load.ctl"), lines);
        Assert.Contains("log=" + Path.Combine(_dir, "load.log"), lines);
        Assert.Contains("bad=" + Path.Combine(_dir, "load.bad"), lines);
        Assert.Contains("direct=true", lines);
        Assert.Contains("errors=50", lines);

        invocation.WriteParFile();
        Assert.DoesNotContain(Password, File.ReadAllText(invocation.ParFile), StringComparison.Ordinal);
        Assert.True(invocation.DeleteParFile());
        Assert.False(File.Exists(invocation.ParFile));
    }

    [Fact]
    public void Environment_sets_nls_and_tns_admin()
    {
        var start = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.StandardInput, tnsAdmin: @"C:\tns")).Start;

        Assert.Equal("AMERICAN_AMERICA.AL32UTF8", start.Environment["NLS_LANG"]);
        Assert.Equal(".,", start.Environment["NLS_NUMERIC_CHARACTERS"]);
        Assert.Equal(@"C:\tns", start.Environment["TNS_ADMIN"]);
        Assert.False(SqlLoaderCommand.Build(Spec(LoaderPasswordVia.StandardInput)).Start.Environment.ContainsKey("TNS_ADMIN"));
    }

    [Fact]
    public void Descriptor_connect_strings_are_quoted_and_flattened()
    {
        var descriptor = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db)(PORT=1521))\r\n  (CONNECT_DATA=(SERVICE_NAME=ORCL)))";
        var text = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.PrivateParfile, descriptor)).LoggableParFileText;

        Assert.StartsWith("userid=SCOTT/\"" + SqlLoaderCommand.PasswordMask + "\"@\"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db)(PORT=1521)) (CONNECT_DATA=(SERVICE_NAME=ORCL)))\"", text, StringComparison.Ordinal);
        Assert.Equal("SCOTT/\"" + Password + "\"@ORCL_ALIAS\n", SqlLoaderCommand.Build(Spec(LoaderPasswordVia.StandardInput, "ORCL_ALIAS")).Start.StandardInput);
    }

    [Fact]
    public void Private_parfile_mode_puts_the_password_only_in_an_owner_only_file()
    {
        var invocation = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.PrivateParfile));

        AssertNoPassword(invocation);
        Assert.Null(invocation.Start.StandardInput);
        Assert.StartsWith("userid=SCOTT/\"" + SqlLoaderCommand.PasswordMask + "\"@dbhost:1521/ORCLPDB1", invocation.LoggableParFileText, StringComparison.Ordinal);

        invocation.WriteParFile();
        var written = File.ReadAllText(invocation.ParFile);
        Assert.StartsWith("userid=SCOTT/\"" + Password + "\"@dbhost:1521/ORCLPDB1", written, StringComparison.Ordinal);
        AssertOwnerOnly(invocation.ParFile);

        Assert.True(invocation.DeleteParFile());
        Assert.False(File.Exists(invocation.ParFile));
    }

    [Fact]
    public void Writing_the_parfile_again_replaces_a_leftover_file()
    {
        var invocation = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.PrivateParfile));
        File.WriteAllText(invocation.ParFile, "old");

        invocation.WriteParFile();

        Assert.Contains("userid=", File.ReadAllText(invocation.ParFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_dispose_removes_a_private_parfile_too()
    {
        var workspace = LoaderWorkspace.Create("run-par", 1, "EMP", _dir);
        var invocation = SqlLoaderCommand.Build(Spec(LoaderPasswordVia.PrivateParfile) with { ParFile = workspace.ParFile });
        invocation.WriteParFile();

        workspace.Dispose();

        Assert.Null(workspace.CleanupFailure);
        Assert.False(File.Exists(workspace.ParFile));
    }

    [Theory]
    [InlineData(LoaderPasswordVia.StandardInput)]
    [InlineData(LoaderPasswordVia.PrivateParfile)]
    public void Bad_inputs_fail_without_the_password_in_the_message(LoaderPasswordVia via)
    {
        var withNewline = Spec(via) with { Login = new ExternalLoadLogin("SCOTT", "db", Password + "\nx", null) };
        var ex = Assert.Throws<ArgumentException>(() => SqlLoaderCommand.Build(withNewline));
        Assert.DoesNotContain(Password, ex.Message, StringComparison.Ordinal);

        var badUser = Spec(via) with { Login = new ExternalLoadLogin("SCOTT/x", "db", Password, null) };
        ex = Assert.Throws<ArgumentException>(() => SqlLoaderCommand.Build(badUser));
        Assert.DoesNotContain(Password, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LoaderPasswordVia.StandardInput)]
    [InlineData(LoaderPasswordVia.PrivateParfile)]
    public void A_quote_in_the_password_is_refused_on_both_ways(LoaderPasswordVia via)
    {
        // 두 길 모두 userid 안에서 비밀번호를 큰따옴표로 감싼다.
        var login = new ExternalLoadLogin("SCOTT", "db", "a\"b", null);
        var ex = Assert.Throws<ArgumentException>(() => SqlLoaderCommand.Build(Spec(via) with { Login = login }));
        Assert.DoesNotContain("a\"b", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnlyAcl(path);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User;
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Equal<IdentityReference?>(user, r.IdentityReference));
    }
}
