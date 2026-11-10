using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Oranda.Core.Composition;
using Oranda.Core.Storage;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Platform.Storage.Copy;

/// <summary>
/// 복사 실행 기록(<c>logs/copy-runs/{yyyyMMdd-HHmmss}-{jobId}.jsonl</c>). 첫 줄은 머리글, 그 뒤 한 줄에 사건 하나를 덧붙이고
/// 줄마다 디스크까지 flush 한다(Review Focus 3: 트리거·FK 를 끈 직후 앱이 죽어도 끈 목록이 남는다).
/// 마지막 <c>complete</c> 줄이 없는 기록이 「끝나지 않은 실행」이다. 행 값은 쓰지 않는다:
/// 실패 행은 키 열 값과 오류 메시지만 남긴다(설계 9장).
/// </summary>
[OrandaService(typeof(ICopyRunLog))]
internal sealed class CopyRunLog : ICopyRunLog
{
    private const string Extension = ".jsonl";

    /// <summary>지금 열려 있는(아직 Dispose 되지 않은) 기록. 진행 중인 실행을 끝나지 않은 실행으로 보이지 않기 위한 표시.</summary>
    private static readonly ConcurrentDictionary<string, byte> OpenRuns = new(StringComparer.OrdinalIgnoreCase);

    private readonly IAppDataPaths _paths;

    public CopyRunLog(IAppDataPaths paths)
    {
        _paths = paths;
    }

    public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.JobId);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_paths.CopyRunsDirectory);
        var stamp = header.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jobPart = SafeJobId(header.JobId);

        FileStream? stream = null;
        string path = string.Empty;
        for (var n = 1; stream is null; n++)
        {
            // 같은 초에 같은 작업을 다시 시작해도 파일이 겹치지 않게 한다. 작업 Id 는 항상 파일 이름 끝에 둔다.
            var name = n == 1 ? $"{stamp}-{jobPart}{Extension}" : $"{stamp}.{n.ToString(CultureInfo.InvariantCulture)}-{jobPart}{Extension}";
            path = Path.Combine(_paths.CopyRunsDirectory, name);
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // 이미 있는 파일: 다음 번호로.
            }
        }

        OpenRuns[path] = 0;
        var record = new CopyRunRecord(stream, path);
        return StartAsync(record, header, ct);
    }

    public async Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var result = new List<UnfinishedRun>();
        if (!Directory.Exists(_paths.CopyRunsDirectory))
        {
            return result;
        }

        var suffix = "-" + SafeJobId(jobId) + Extension;
        foreach (var file in Directory.EnumerateFiles(_paths.CopyRunsDirectory, "*" + Extension).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetFileName(file).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || OpenRuns.ContainsKey(file))
            {
                continue;
            }

            var run = await ReadUnfinishedAsync(file, jobId, ct).ConfigureAwait(false);
            if (run is not null)
            {
                result.Add(run);
            }
        }

        return result;
    }

    public async Task MarkRecoveredAsync(string runFile, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFile);
        if (!File.Exists(runFile))
        {
            return;
        }

        await using var stream = new FileStream(runFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
        var line = BuildLine("recovered", writer => writer.WriteString("at", Now()));
        if (NeedsLeadingNewline(runFile))
        {
            // 앱이 죽으며 마지막 줄이 잘려 있으면 줄을 먼저 끊어, recovered 가 잘린 조각에 붙지 않게 한다.
            line = [(byte)'\n', .. line];
        }

        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool NeedsLeadingNewline(string file)
    {
        using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (read.Length == 0)
        {
            return false;
        }

        read.Seek(-1, SeekOrigin.End);
        return read.ReadByte() != '\n';
    }

    private static async Task<ICopyRunRecord> StartAsync(CopyRunRecord record, CopyRunHeader header, CancellationToken ct)
    {
        try
        {
            await record.WriteHeaderAsync(header, ct).ConfigureAwait(false);
            return record;
        }
        catch
        {
            await record.DisposeAsync().ConfigureAwait(false);

            // 머리글 없는 빈 기록을 남기지 않는다.
            try
            {
                File.Delete(record.FilePath);
            }
            catch (IOException)
            {
                // 정리 실패는 무시한다.
            }

            throw;
        }
    }

    private static async Task<UnfinishedRun?> ReadUnfinishedAsync(string file, string jobId, CancellationToken ct)
    {
        var startedAt = default(DateTimeOffset?);
        var hasHeader = false;
        var disabled = new List<DisabledObject>();
        var finished = false;

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 앱이 죽으며 마지막 줄이 잘렸을 수 있다. 읽을 수 있는 줄만 쓴다.
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
                {
                    continue;
                }

                switch (typeProp.GetString())
                {
                    case "header":
                        hasHeader = true;
                        if (root.TryGetProperty("jobId", out var id) && id.GetString() is { } headerJobId
                            && !string.Equals(headerJobId, jobId, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        if (root.TryGetProperty("startedAt", out var started) && started.TryGetDateTimeOffset(out var at))
                        {
                            startedAt = at;
                        }

                        break;
                    case "disabled":
                        ReadDisabled(root, disabled);
                        break;
                    case "complete":
                    case "recovered":
                        finished = true;
                        break;
                    default:
                        break;
                }
            }
        }

        // 머리글이 없으면 어느 작업의 기록인지 알 수 없다.
        if (finished || !hasHeader)
        {
            return null;
        }

        return new UnfinishedRun(file, startedAt ?? new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero), disabled);
    }

    private static void ReadDisabled(JsonElement root, List<DisabledObject> into)
    {
        if (!root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in objects.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("kind", out var kind) && Enum.TryParse<DisabledKind>(kind.GetString(), out var parsedKind)
                && item.TryGetProperty("owner", out var owner) && owner.GetString() is { } o
                && item.TryGetProperty("table", out var table) && table.GetString() is { } t
                && item.TryGetProperty("name", out var name) && name.GetString() is { } n)
            {
                var entry = new DisabledObject(parsedKind, o, t, n);
                if (!into.Contains(entry))
                {
                    into.Add(entry);
                }
            }
        }
    }

    private static string SafeJobId(string jobId)
    {
        var builder = new StringBuilder(jobId.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in jobId)
        {
            builder.Append(char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch);
        }

        // 파일 이름 한도(255바이트)를 넘지 않게 자른다. 작업 Id 는 보통 32자다.
        return builder.Length > 64 ? builder.ToString(0, 64) : builder.ToString();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static byte[] BuildLine(string type, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            body(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteDateTime(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } v)
        {
            writer.WriteString(name, v.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private sealed class CopyRunRecord : ICopyRunRecord
    {
        private readonly FileStream _stream;
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public string FilePath => _path;

        public CopyRunRecord(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public Task WriteHeaderAsync(CopyRunHeader header, CancellationToken ct)
            => AppendAsync(
                BuildLine("header", w =>
                {
                    w.WriteNumber("schemaVersion", CopyJobSchema.Current);
                    w.WriteString("jobId", header.JobId);
                    w.WriteString("jobName", header.JobName);
                    w.WriteString("source", header.SourceName);
                    w.WriteString("target", header.TargetName);
                    WriteDateTime(w, "periodFrom", header.Period.From);
                    WriteDateTime(w, "periodTo", header.Period.To);
                    if (header.Period.DayStartHour != 0) w.WriteNumber("dayStartHour", header.Period.DayStartHour);
                    w.WriteString("startedAt", header.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                }),
                ct);

        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(objects);
            return AppendAsync(
                BuildLine("disabled", w =>
                {
                    w.WriteString("at", Now());
                    w.WriteStartArray("objects");
                    foreach (var o in objects)
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", o.Kind.ToString());
                        w.WriteString("owner", o.Owner);
                        w.WriteString("table", o.Table);
                        w.WriteString("name", o.Name);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }),
                ct);
        }

        public Task RecordStatementAsync(string sql, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(sql);
            return AppendAsync(BuildLine("statement", w =>
            {
                w.WriteString("at", Now());
                w.WriteString("sql", sql);
            }), ct);
        }

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(result);
            return AppendAsync(BuildLine("table", w =>
            {
                w.WriteString("at", Now());
                w.WriteString("table", result.Table);
                w.WriteNumber("sourceRows", result.SourceRows);
                w.WriteNumber("targetRows", result.TargetRows);
                w.WriteNumber("elapsedMs", (long)result.Elapsed.TotalMilliseconds);
                w.WriteString("outcome", result.Outcome.ToString());
                if (result.Message is { } message)
                {
                    w.WriteString("message", message);
                }

                if (result.Times is { } times)
                {
                    // 시간 나눔(결정 74): 겹치기 켬·끔 전후 비교용(밀리초·행/초).
                    w.WriteNumber("readMs", (long)times.Read.TotalMilliseconds);
                    w.WriteNumber("convertMs", (long)times.Convert.TotalMilliseconds);
                    w.WriteNumber("writeMs", (long)times.Write.TotalMilliseconds);
                    w.WriteNumber("rowsPerSecond", Math.Round(times.RowsPerSecond, 1));
                }
            }), ct);
        }

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(row);
            return AppendAsync(BuildLine("failedRow", w =>
            {
                w.WriteString("at", Now());
                w.WriteString("table", row.Table);

                // 키 열 값만 남긴다. 다른 열 값은 이 형식에 들어올 자리가 없다.
                w.WriteStartObject("key");
                foreach (var (column, value) in row.Key)
                {
                    if (value is null)
                    {
                        w.WriteNull(column);
                    }
                    else
                    {
                        w.WriteString(column, value);
                    }
                }

                w.WriteEndObject();
                w.WriteString("error", row.Error);
            }), ct);
        }

        public Task RecordEventAsync(string kind, string? table, string text, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(text);
            return AppendAsync(BuildLine("event", w =>
            {
                w.WriteString("at", Now());
                w.WriteString("kind", kind);
                if (table is not null)
                {
                    w.WriteString("table", table);
                }

                w.WriteString("text", text);
            }), ct);
        }

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct)
            => AppendAsync(BuildLine("complete", w =>
            {
                w.WriteString("at", Now());
                w.WriteString("outcome", outcome.ToString());
            }), ct);

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                OpenRuns.TryRemove(_path, out _);
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task AppendAsync(byte[] line, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await _stream.WriteAsync(line, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);

                // 줄마다 디스크까지 내려 둔다: 앱이 죽어도 끈 목록·진행 상황이 남아야 한다.
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
