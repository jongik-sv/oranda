using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. 도구 찾기 순서·설정 경로·버전 줄 읽기(가짜 환경·가짜 실행기).
public class ExternalToolProbeTests
{
    private sealed class FakeEnvironment : IExternalToolEnvironment
    {
        public bool IsWindows { get; init; } = true;

        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetVariable(string name) => Variables.TryGetValue(name, out var v) ? v : null;

        public bool FileExists(string path) => Files.Contains(path);

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public IReadOnlyList<string> GetDirectories(string path, string searchPattern)
        {
            var prefix = searchPattern.TrimEnd('*');
            return Directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            || (path.EndsWith('\\') && string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase)))
                .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private sealed class FakeRunner(Func<ExternalProcessStart, ExternalProcessResult> run) : IExternalProcessRunner
    {
        public List<ExternalProcessStart> Starts { get; } = [];

        public Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Starts.Add(start);
            return Task.FromResult(run(start));
        }
    }

    private static FakeRunner Output(string stdout, int exit = 1, bool timedOut = false) =>
        new(_ => new ExternalProcessResult(exit, stdout, string.Empty, timedOut));

    [Fact]
    public void Path_entries_are_searched_first()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = @"C:\Windows;""C:\ic\instantclient_19_3"";C:\other";
        env.Variables["ORACLE_HOME"] = @"C:\oracle\db";
        env.Files.Add(@"C:\ic\instantclient_19_3\sqlldr.exe");
        env.Files.Add(@"C:\oracle\db\bin\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, null);

        Assert.Equal(@"C:\ic\instantclient_19_3\sqlldr.exe", location.Path);
        Assert.False(location.FromSettings);
        Assert.Equal([@"C:\Windows", @"C:\ic\instantclient_19_3"], location.Searched);
    }

    [Fact]
    public void Oracle_home_bin_comes_after_path()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = @"C:\Windows";
        env.Variables["ORACLE_HOME"] = @"C:\oracle\db";
        env.Files.Add(@"C:\oracle\db\bin\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, null);

        Assert.Equal(@"C:\oracle\db\bin\sqlldr.exe", location.Path);
    }

    [Fact]
    public void Instant_client_folders_are_found_newest_first()
    {
        var env = new FakeEnvironment();
        env.Directories.Add(@"C:\oracle\instantclient_9_2");
        env.Directories.Add(@"C:\oracle\instantclient_19_3");
        env.Directories.Add(@"C:\oracle\instantclient_23_4");
        env.Files.Add(@"C:\oracle\instantclient_19_3\sqlldr.exe");
        env.Files.Add(@"C:\oracle\instantclient_23_4\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, null);

        Assert.Equal(@"C:\oracle\instantclient_23_4\sqlldr.exe", location.Path);
    }

    [Fact]
    public void A_sibling_of_the_oci_folder_on_path_is_searched()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = @"D:\tools\instantclient_21_9";
        env.Files.Add(@"D:\tools\instantclient_21_9\oci.dll");
        env.Directories.Add(@"D:\tools\instantclient_21_9");
        env.Directories.Add(@"D:\tools\instantclient_21_9_tools");
        env.Files.Add(@"D:\tools\instantclient_21_9_tools\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, null);

        Assert.Equal(@"D:\tools\instantclient_21_9_tools\sqlldr.exe", location.Path);
    }

    [Fact]
    public void TbLoader_looks_in_tb_home()
    {
        var env = new FakeEnvironment();
        env.Variables["TB_HOME"] = @"C:\tibero7";
        env.Files.Add(@"C:\tibero7\bin\tbloader.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.TbLoader, "  ");

        Assert.Equal(@"C:\tibero7\bin\tbloader.exe", location.Path);
        Assert.Equal([@"C:\tibero7\client\bin", @"C:\tibero7\bin"], location.Searched);
    }

    [Fact]
    public void Non_windows_names_have_no_exe()
    {
        var env = new FakeEnvironment { IsWindows = false };
        env.Variables["PATH"] = "/usr/bin:/opt/oracle/ic";
        env.Files.Add(Path.Combine("/opt/oracle/ic", "sqlldr"));

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, null);

        Assert.Equal(Path.Combine("/opt/oracle/ic", "sqlldr"), location.Path);
    }

    [Theory]
    [InlineData(@"C:\ic\sqlldr.exe")]
    [InlineData(@"""C:\ic\sqlldr.exe""")]
    [InlineData(@"C:\ic\sqlldr")]
    [InlineData(@"C:\ic")]
    public void Configured_path_may_be_the_exe_or_its_folder(string configured)
    {
        var env = new FakeEnvironment();
        env.Directories.Add(@"C:\ic");
        env.Files.Add(@"C:\ic\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, configured);

        Assert.Equal(@"C:\ic\sqlldr.exe", location.Path);
        Assert.True(location.FromSettings);
    }

    [Fact]
    public void A_wrong_configured_path_is_not_searched_around()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = @"C:\ic";
        env.Files.Add(@"C:\ic\sqlldr.exe");

        var location = new ExternalToolProbe(Output(string.Empty), env).Locate(ExternalLoaderKind.SqlLoader, @"C:\nowhere\sqlldr.exe");

        Assert.Null(location.Path);
        Assert.True(location.FromSettings);
        Assert.Equal([@"C:\nowhere\sqlldr.exe"], location.Searched);
    }

    [Fact]
    public async Task Check_reads_the_19c_banner()
    {
        var env = new FakeEnvironment();
        env.Files.Add(@"C:\ic\sqlldr.exe");
        var runner = Output("""

            SQL*Loader: Release 19.0.0.0.0 - Production on Tue Nov 10 21:14:03 2026
            Version 19.3.0.0.0

            Copyright (c) 1982, 2019, Oracle and/or its affiliates.  All rights reserved.

            Usage: SQLLDR keyword=value [,keyword=value,...]
            """);

        var info = await new ExternalToolProbe(runner, env).CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", CancellationToken.None);

        Assert.True(info.Usable, info.Problem);
        Assert.Equal(19, info.Version);
        Assert.StartsWith("SQL*Loader: Release 19.0.0.0.0", info.VersionLine, StringComparison.Ordinal);
        var start = Assert.Single(runner.Starts);
        Assert.Empty(start.Arguments);
        Assert.Null(start.StandardInput);
        Assert.Equal(ExternalToolProbe.CheckTimeout, start.Timeout);
    }

    [Fact]
    public async Task Check_reads_the_23ai_banner()
    {
        var env = new FakeEnvironment();
        env.Directories.Add(@"C:\ic");
        env.Files.Add(@"C:\ic\sqlldr.exe");
        var runner = Output("SQL*Loader: Release 23.0.0.0.0 - Production on Tue Nov 10 21:14:03 2026\nVersion 23.4.0.24.05\n");

        var info = await new ExternalToolProbe(runner, env).CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic", CancellationToken.None);

        Assert.Equal(23, info.Version);
    }

    [Fact]
    public void Version_falls_back_to_the_version_line()
    {
        var (line, version) = ExternalToolProbe.ParseSqlLoaderVersion("SQL*Loader: Production\nVersion 21.3.0.0.0\n");

        Assert.Equal("SQL*Loader: Production", line);
        Assert.Equal(21, version);
    }

    [Theory]
    [InlineData("tbLoader 7.2.1 (Build 1234)\nusage: tbloader ...", 7)]
    [InlineData("TBLOADER Version 6 FS07\n", 6)]
    [InlineData("Tibero client 7.1.0\n", 7)]
    public void TbLoader_versions_are_read_tolerantly(string output, int version)
    {
        var (line, parsed) = ExternalToolProbe.ParseTbLoaderVersion(output);

        Assert.NotNull(line);
        Assert.Equal(version, parsed);
    }

    [Fact]
    public async Task Not_found_lists_the_searched_places()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = @"C:\Windows;C:\bin";

        var info = await new ExternalToolProbe(Output(string.Empty), env).CheckAsync(ExternalLoaderKind.SqlLoader, null, CancellationToken.None);

        Assert.False(info.Usable);
        Assert.Contains("찾지 못했습니다", info.Problem, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows", info.Problem, StringComparison.Ordinal);
        Assert.Contains("Instant Client", info.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_failure_becomes_a_problem()
    {
        var env = new FakeEnvironment();
        env.Files.Add(@"C:\ic\sqlldr.exe");
        var runner = new FakeRunner(_ => throw new ExternalProcessException("실행 파일 형식이 잘못되었습니다."));

        var info = await new ExternalToolProbe(runner, env).CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", CancellationToken.None);

        Assert.Equal(@"C:\ic\sqlldr.exe", info.Path);
        Assert.Contains("실행에 실패", info.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_without_a_version_line_is_a_problem()
    {
        var env = new FakeEnvironment();
        env.Files.Add(@"C:\ic\sqlldr.exe");

        var info = await new ExternalToolProbe(Output("The program can't start because oci.dll is missing.", exit: -1073741515), env)
            .CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", CancellationToken.None);

        Assert.False(info.Usable);
        Assert.Contains("버전 줄", info.Problem, StringComparison.Ordinal);
        Assert.Contains("oci.dll", info.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_without_a_version_line_is_a_problem()
    {
        var env = new FakeEnvironment();
        env.Files.Add(@"C:\ic\sqlldr.exe");

        var info = await new ExternalToolProbe(Output("Username:", timedOut: true), env)
            .CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", CancellationToken.None);

        Assert.Contains("10초", info.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_an_exception()
    {
        var env = new FakeEnvironment();
        env.Files.Add(@"C:\ic\sqlldr.exe");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ExternalToolProbe(Output(string.Empty), env).CheckAsync(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", cts.Token));
    }
}
