using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.SqlPlus;

/// <summary>
/// SQL*Plus 명령 처리(SET SERVEROUTPUT, DEFINE/UNDEFINE, PROMPT, @파일·@@·START, SPOOL, WHENEVER SQLERROR, EXEC, EXIT).
/// 상태(DEFINE 값, WHENEVER, SPOOL, 현재 스크립트 폴더)는 세션마다 따로 갖는다.
/// 실행 엔진은 이 처리기를 부르고, 이 처리기는 파일 안 문장을 다시 실행 엔진으로 보낸다(순환을 피하려고 서비스는 처음 쓸 때 꺼낸다).
/// 소유: TSK-06-06.
/// </summary>
[OrandaService(typeof(ISqlPlusCommandProcessor))]
internal sealed class SqlPlusCommandProcessor : ISqlPlusCommandProcessor
{
    /// <summary>@파일 안에서 다시 @파일 을 부를 수 있는 깊이(SQL*Plus 도 같은 보호가 있다).</summary>
    internal const int MaxDepth = 20;

    private static readonly HashSet<string> IgnoredVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "COLUMN", "COL", "TTITLE", "BTITLE", "TIMING", "PAUSE", "CLEAR", "CL", "STORE", "BREAK", "COMPUTE", "REPHEADER", "REPFOOTER", "REM", "REMARK", "SQLPLUS",
    };

    private static readonly HashSet<string> UnsupportedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCEPT", "ACC", "VARIABLE", "VAR", "PRINT", "DESC", "DESCRIBE", "SHOW", "CONNECT", "CONN", "DISCONNECT", "HOST",
    };

    private readonly Func<IExecutionService> _execution;
    private readonly Func<ILanguageService> _language;
    private readonly Func<IEventBus?> _events;
    private readonly ConditionalWeakTable<ISession, SessionState> _states = new();

    public SqlPlusCommandProcessor(IServiceProvider services)
        : this(
            () => (IExecutionService)services.GetService(typeof(IExecutionService))!,
            () => (ILanguageService)services.GetService(typeof(ILanguageService))!,
            () => services.GetService(typeof(IEventBus)) as IEventBus)
    {
    }

    internal SqlPlusCommandProcessor(Func<IExecutionService> execution, Func<ILanguageService> language, Func<IEventBus?>? events = null)
    {
        _execution = execution;
        _language = language;
        _events = events ?? (() => null);
    }

    public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = _states.GetValue(s, _ => new SessionState());
        var result = await DispatchAsync(s, state, stmt, ct).ConfigureAwait(false);
        return result.Elapsed == TimeSpan.Zero ? result with { Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started) } : result;
    }

    /// <summary>시험·진단용: 세션의 DEFINE 값(없으면 null).</summary>
    internal string? GetDefine(ISession s, string name)
        => _states.TryGetValue(s, out var st) && st.Defines.TryGetValue(name, out var v) ? v : null;

    private async Task<ExecutionResult> DispatchAsync(ISession s, SessionState state, SqlStatement stmt, CancellationToken ct)
    {
        var text = stmt.Text.Trim();
        if (text.Length == 0)
        {
            return Ok(stmt);
        }

        string verb;
        string rest;
        if (text[0] == '@')
        {
            var relative = text.StartsWith("@@", StringComparison.Ordinal);
            verb = relative ? "@@" : "@";
            rest = text[verb.Length..].Trim();
        }
        else
        {
            var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
            verb = (split < 0 ? text : text[..split]).ToUpperInvariant();
            rest = split < 0 ? string.Empty : text[split..].Trim();
        }

        try
        {
            switch (verb)
            {
                case "@":
                case "@@":
                case "START":
                case "STA":
                    return await RunFileAsync(s, state, stmt, rest, ct).ConfigureAwait(false);
                case "SET":
                    return await SetAsync(s, state, stmt, rest, ct).ConfigureAwait(false);
                case "DEFINE":
                case "DEF":
                    return Define(state, stmt, rest);
                case "UNDEFINE":
                case "UNDEF":
                    foreach (var name in rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        state.Defines.Remove(name);
                    }

                    return Ok(stmt);
                case "PROMPT":
                case "PRO":
                    return Ok(stmt, rest);
                case "SPOOL":
                case "SPO":
                    return Spool(s, state, stmt, rest);
                case "WHENEVER":
                    return Whenever(state, stmt, rest);
                case "EXEC":
                case "EXECUTE":
                    return await ExecAsync(s, stmt, rest, ct).ConfigureAwait(false);
                case "EXIT":
                case "QUIT":
                    state.StopRequested = true;
                    return Ok(stmt);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(stmt, "SP2-0310", ex.Message);
        }

        if (IgnoredVerbs.Contains(verb))
        {
            return Ok(stmt);
        }

        return Fail(stmt, UnsupportedVerbs.Contains(verb) ? "ORANDA-SQLPLUS" : "SP2-0734",
            UnsupportedVerbs.Contains(verb)
                ? $"SQL*Plus 명령 '{verb}' 는 지원하지 않습니다."
                : $"알 수 없는 명령 '{verb}' — 나머지 줄은 무시됩니다.");
    }

    // ---- SET / DEFINE / WHENEVER / EXEC ----

    private async Task<ExecutionResult> SetAsync(ISession s, SessionState state, SqlStatement stmt, string rest, CancellationToken ct)
    {
        var parts = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Fail(stmt, "SP2-0158", "SET 옵션 이름이 없습니다.");
        }

        var option = parts[0].ToUpperInvariant();
        var value = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
        switch (option)
        {
            case "SERVEROUTPUT" or "SERVEROUT":
                if (value is "ON")
                {
                    var size = "NULL";
                    var sizeIndex = Array.FindIndex(parts, p => p.Equals("SIZE", StringComparison.OrdinalIgnoreCase));
                    if (sizeIndex >= 0 && sizeIndex + 1 < parts.Length && !parts[sizeIndex + 1].Equals("UNLIMITED", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(parts[sizeIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        {
                            return Fail(stmt, "SP2-0267", $"SIZE 값이 올바르지 않습니다: {parts[sizeIndex + 1]}");
                        }

                        size = Math.Clamp(n, 2000, 1_000_000).ToString(CultureInfo.InvariantCulture);
                    }

                    state.ServerOutput = true;
                    return await RunInternalAsync(s, stmt, $"BEGIN DBMS_OUTPUT.ENABLE({size}); END;", ct).ConfigureAwait(false);
                }

                if (value is "OFF")
                {
                    state.ServerOutput = false;
                    return await RunInternalAsync(s, stmt, "BEGIN DBMS_OUTPUT.DISABLE; END;", ct).ConfigureAwait(false);
                }

                return Fail(stmt, "SP2-0158", "SET SERVEROUTPUT 는 ON 또는 OFF 입니다.");
            case "STATISTICS" or "STAT":
                if (value is not ("ON" or "OFF"))
                {
                    return Fail(stmt, "SP2-0158", "SET STATISTICS 는 ON 또는 OFF 입니다.");
                }

                state.Statistics = value == "ON";
                return Ok(stmt);
            default:
                return Ok(stmt); // LINESIZE, PAGESIZE, FEEDBACK 등 표시 옵션은 받아들이고 무시한다.
        }
    }

    private static ExecutionResult Define(SessionState state, SqlStatement stmt, string rest)
    {
        if (rest.Length == 0)
        {
            return Ok(stmt, state.Defines.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => Describe(kv.Key, kv.Value)).ToArray());
        }

        var eq = rest.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            var name = rest.Trim();
            return state.Defines.TryGetValue(name, out var existing)
                ? Ok(stmt, Describe(name, existing))
                : Fail(stmt, "SP2-0135", $"기호 {name} 은(는) UNDEFINED 입니다.");
        }

        var key = rest[..eq].Trim();
        if (key.Length == 0 || key.Contains(' ', StringComparison.Ordinal))
        {
            return Fail(stmt, "SP2-0226", "DEFINE 이름이 올바르지 않습니다.");
        }

        state.Defines[key] = Unquote(rest[(eq + 1)..].Trim());
        return Ok(stmt);
    }

    private static string Describe(string name, string value)
        => string.Create(CultureInfo.InvariantCulture, $"DEFINE {name.ToUpperInvariant(),-20} = \"{value}\" (CHAR)");

    private static string Unquote(string v)
        => v.Length >= 2 && ((v[0] == '\'' && v[^1] == '\'') || (v[0] == '"' && v[^1] == '"')) ? v[1..^1] : v;

    private static ExecutionResult Whenever(SessionState state, SqlStatement stmt, string rest)
    {
        var parts = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return Fail(stmt, "SP2-0158", "WHENEVER SQLERROR|OSERROR EXIT|CONTINUE 형식이어야 합니다.");
        }

        var target = parts[0].ToUpperInvariant();
        var action = parts[1].ToUpperInvariant();
        if (target == "OSERROR")
        {
            return Ok(stmt); // OS 오류는 다루지 않는다.
        }

        if (target != "SQLERROR" || action is not ("EXIT" or "CONTINUE"))
        {
            return Fail(stmt, "SP2-0158", "WHENEVER SQLERROR 다음은 EXIT 또는 CONTINUE 입니다.");
        }

        var tail = parts.Select(p => p.ToUpperInvariant()).ToList();
        state.ExitOnError = action == "EXIT";
        state.OnErrorTransaction = tail.Contains("COMMIT") ? TransactionAction.Commit
            : tail.Contains("ROLLBACK") ? TransactionAction.Rollback
            : TransactionAction.None;
        return Ok(stmt);
    }

    private async Task<ExecutionResult> ExecAsync(ISession s, SqlStatement stmt, string rest, CancellationToken ct)
    {
        var call = rest.TrimEnd().TrimEnd(';');
        return call.Length == 0
            ? Fail(stmt, "SP2-0158", "EXEC 다음에 PL/SQL 문이 필요합니다.")
            : await RunInternalAsync(s, stmt, $"BEGIN {call}; END;", ct).ConfigureAwait(false);
    }

    /// <summary>서버에 보내는 내부 PL/SQL(이력에 남기지 않는다 — SQL*Plus 문장 자신이 이력에 남는다).</summary>
    private async Task<ExecutionResult> RunInternalAsync(ISession s, SqlStatement owner, string plsql, CancellationToken ct)
    {
        var inner = new SqlStatement(plsql, default, StatementKind.PlSql) { Verb = "BEGIN" };
        var r = await _execution().ExecuteAsync(s, inner, new Dictionary<string, BindValue>(), InternalOptions, ct).ConfigureAwait(false);
        return r with { Statement = owner };
    }

    private static ExecutionOptions InternalOptions { get; } = new() { Source = HistorySource.Script, SkipHistory = true };

    // ---- SPOOL ----

    private ExecutionResult Spool(ISession s, SessionState state, SqlStatement stmt, string rest)
    {
        if (rest.Length == 0)
        {
            return Ok(stmt, state.SpoolFile is null ? "현재 스풀링 안 함" : $"현재 스풀: {state.SpoolFile}");
        }

        var parts = SplitPathAndArgs(rest);
        var first = parts[0];
        if (first.Equals("OFF", StringComparison.OrdinalIgnoreCase) || first.Equals("OUT", StringComparison.OrdinalIgnoreCase))
        {
            state.CloseSpool();
            return Ok(stmt);
        }

        var append = parts.Skip(1).Any(p => p.Equals("APPEND", StringComparison.OrdinalIgnoreCase));
        var create = parts.Skip(1).Any(p => p.Equals("CREATE", StringComparison.OrdinalIgnoreCase));
        var path = ResolvePath(state, first, forWrite: true)!;
        if (create && File.Exists(path))
        {
            return Fail(stmt, "SP2-0557", $"파일 '{path}' 이(가) 이미 있습니다(CREATE).");
        }

        state.CloseSpool();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        state.SpoolFile = path;
        state.SpoolWriter = new StreamWriter(path, append, new UTF8Encoding(false)) { AutoFlush = true };
        state.SpoolSubscription = _events()?.Subscribe<StatementExecuted>(state, e => WriteSpool(s, state, e));
        return Ok(stmt);
    }

    /// <summary>스풀 중인 세션의 실행 결과(DBMS_OUTPUT, 오류, 영향 행 수)를 파일에 덧붙인다. @파일 자신은 안의 문장들이 따로 낸다.</summary>
    private static void WriteSpool(ISession s, SessionState state, StatementExecuted e)
    {
        if (e.SessionId != s.Id || state.SpoolWriter is not { } w || IsFileRun(e.Statement))
        {
            return;
        }

        foreach (var line in e.Result.DbmsOutput)
        {
            w.WriteLine(line);
        }

        if (e.Result.Error is { } err)
        {
            w.WriteLine($"{err.Code}: {err.Message}");
        }
        else if (e.Result.RowsAffected is { } n && e.Statement.Kind == StatementKind.Dml)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n}행이 처리되었습니다."));
        }
    }

    private static bool IsFileRun(SqlStatement st)
    {
        if (st.Kind != StatementKind.SqlPlus)
        {
            return false;
        }

        var t = st.Text.TrimStart();
        return t.StartsWith('@') || t.StartsWith("START", StringComparison.OrdinalIgnoreCase) || t.StartsWith("STA ", StringComparison.OrdinalIgnoreCase);
    }

    // ---- @파일 ----

    private async Task<ExecutionResult> RunFileAsync(ISession s, SessionState state, SqlStatement stmt, string rest, CancellationToken ct)
    {
        if (rest.Length == 0)
        {
            return Fail(stmt, "SP2-0310", "@ 다음에 파일 이름이 필요합니다.");
        }

        var args = SplitPathAndArgs(rest);
        var path = ResolvePath(state, args[0], forWrite: false);
        if (path is null)
        {
            return Fail(stmt, "SP2-0310", $"파일 '{args[0]}' 을(를) 열 수 없습니다.");
        }

        if (state.Depth >= MaxDepth)
        {
            return Fail(stmt, "SP2-0309", $"@파일 이 {MaxDepth}단계를 넘게 중첩되었습니다.");
        }

        var script = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var statements = _language().Split(script, new SplitOptions { RecognizeSqlPlus = true });
        for (var i = 1; i < args.Count; i++)
        {
            state.Defines[i.ToString(CultureInfo.InvariantCulture)] = args[i];
        }

        state.Depth++;
        state.ScriptDirs.Push(Path.GetDirectoryName(path)!);
        var exec = _execution();
        var lines = new List<string>();
        var elapsed = TimeSpan.Zero;
        long? rows = null;
        ResultSetHolder last = new();
        DbError? firstError = null;
        var errors = 0;
        var cancelled = false;
        var stopped = false;
        try
        {
            var before = state.Statistics ? await SessionStatistics.CaptureAsync(exec, s, ct).ConfigureAwait(false) : null;
            foreach (var child in statements)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var r = await RunChildAsync(exec, s, state, child, ct).ConfigureAwait(false);
                elapsed += r.Elapsed;
                if (r.RowsAffected is { } n)
                {
                    rows = (rows ?? 0) + n;
                }

                lines.AddRange(r.DbmsOutput);
                if (r.ResultSet is not null)
                {
                    await last.ReplaceAsync(r.ResultSet).ConfigureAwait(false);
                }

                if (state.Statistics && before is not null)
                {
                    var after = await SessionStatistics.CaptureAsync(exec, s, ct).ConfigureAwait(false);
                    if (after is not null)
                    {
                        lines.AddRange(SessionStatistics.Describe(SessionStatistics.Diff(before, after)));
                    }

                    before = after;
                }

                if (r.Status == ExecutionStatus.Cancelled)
                {
                    cancelled = true;
                    break;
                }

                if (r.Status is ExecutionStatus.Failed or ExecutionStatus.Denied)
                {
                    errors++;
                    var error = r.Error ?? new DbError("POLICY", r.Policy?.Message ?? "정책 관문이 거부했습니다.");
                    firstError ??= error;
                    lines.Add($"{error.Code}: {error.Message}");
                    await ApplyTransactionActionAsync(s, state.OnErrorTransaction, ct).ConfigureAwait(false);
                    if (state.ExitOnError)
                    {
                        stopped = true;
                        break;
                    }
                }

                if (state.StopRequested)
                {
                    stopped = true;
                    break;
                }
            }
        }
        finally
        {
            state.Depth--;
            state.ScriptDirs.Pop();
            if (state.Depth == 0)
            {
                state.StopRequested = false;
            }
        }

        var status = cancelled ? ExecutionStatus.Cancelled : errors > 0 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded;
        var summary = errors > 0
            ? new DbError(firstError!.Code, errors == 1 && !stopped ? firstError.Message : $"{firstError.Message} (오류 {errors}건{(stopped ? ", 중단" : string.Empty)})")
            : null;
        return new ExecutionResult(stmt, status)
        {
            Elapsed = elapsed,
            RowsAffected = rows,
            DbmsOutput = lines,
            ResultSet = last.Take(),
            Error = summary,
        };
    }

    /// <summary>@파일 안 문장 하나. 이 세션의 DEFINE 값을 치환 변수에 먼저 채운다.</summary>
    private static async Task<ExecutionResult> RunChildAsync(IExecutionService exec, ISession s, SessionState state, SqlStatement child, CancellationToken ct)
    {
        if (child.Text.Contains('&', StringComparison.Ordinal))
        {
            var text = SubstitutionText.Apply(child.Text, n => state.Defines.TryGetValue(n, out var v) ? v : null, (n, v) => state.Defines[n] = v);
            child = child with
            {
                Text = text,
                SubstitutionNames = [.. child.SubstitutionNames.Where(n => !state.Defines.ContainsKey(n))],
            };
        }

        try
        {
            return await exec.ExecuteAsync(s, child, new Dictionary<string, BindValue>(), ScriptOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ExecutionResult(child, ExecutionStatus.Cancelled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Fail(child, "ORANDA-SQLPLUS", ex.Message);
        }
    }

    private static ExecutionOptions ScriptOptions { get; } = new() { Source = HistorySource.Script };

    private static async Task ApplyTransactionActionAsync(ISession s, TransactionAction action, CancellationToken ct)
    {
        if (action == TransactionAction.Commit)
        {
            await s.CommitAsync(ct).ConfigureAwait(false);
        }
        else if (action == TransactionAction.Rollback)
        {
            await s.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- 경로·결과 도우미 ----

    /// <summary>따옴표로 묶은 경로를 한 덩어리로 보고 공백으로 나눈다.</summary>
    internal static List<string> SplitPathAndArgs(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString());
        }

        return list;
    }

    /// <summary>
    /// 상대 경로는 지금 실행 중인 스크립트의 폴더, 없거나 거기에 없으면 현재 작업 폴더 기준. 읽을 때 확장자가 없고 그 파일이 없으면 <c>.sql</c>,
    /// 스풀에 확장자가 없으면 <c>.lst</c> 를 붙인다. 읽기에서 못 찾으면 null.
    /// </summary>
    private static string? ResolvePath(SessionState state, string arg, bool forWrite)
    {
        var bases = new List<string>();
        if (state.ScriptDirs.Count > 0)
        {
            bases.Add(state.ScriptDirs.Peek());
        }

        bases.Add(Directory.GetCurrentDirectory());
        if (forWrite)
        {
            var written = Path.GetFullPath(arg, bases[0]);
            return Path.HasExtension(written) ? written : written + ".lst";
        }

        foreach (var dir in bases)
        {
            var path = Path.GetFullPath(arg, dir);
            if (File.Exists(path))
            {
                return path;
            }

            if (!Path.HasExtension(path) && File.Exists(path + ".sql"))
            {
                return path + ".sql";
            }
        }

        return null;
    }

    private static ExecutionResult Ok(SqlStatement stmt, params string[] lines)
        => new(stmt, ExecutionStatus.Succeeded) { DbmsOutput = lines };

    private static ExecutionResult Fail(SqlStatement stmt, string code, string message)
        => new(stmt, ExecutionStatus.Failed) { Error = new DbError(code, message) };

    private enum TransactionAction
    {
        None,
        Commit,
        Rollback,
    }

    /// <summary>마지막 질의 결과를 쥐고 있다가 새 결과가 오면 앞의 것을 닫는다.</summary>
    private sealed class ResultSetHolder
    {
        private Oranda.Core.Grid.IResultSet? _current;

        public async Task ReplaceAsync(Oranda.Core.Grid.IResultSet next)
        {
            if (_current is not null)
            {
                await _current.DisposeAsync().ConfigureAwait(false);
            }

            _current = next;
        }

        public Oranda.Core.Grid.IResultSet? Take()
        {
            var r = _current;
            _current = null;
            return r;
        }
    }

    private sealed class SessionState
    {
        public Dictionary<string, string> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool ServerOutput { get; set; }

        public bool Statistics { get; set; }

        public bool ExitOnError { get; set; }

        public TransactionAction OnErrorTransaction { get; set; }

        public bool StopRequested { get; set; }

        public int Depth { get; set; }

        public Stack<string> ScriptDirs { get; } = new();

        public string? SpoolFile { get; set; }

        public StreamWriter? SpoolWriter { get; set; }

        public IDisposable? SpoolSubscription { get; set; }

        public void CloseSpool()
        {
            SpoolSubscription?.Dispose();
            SpoolSubscription = null;
            SpoolWriter?.Dispose();
            SpoolWriter = null;
            SpoolFile = null;
        }
    }
}
