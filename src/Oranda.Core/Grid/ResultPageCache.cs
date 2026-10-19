using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Oranda.Core.Grid;

/// <summary>페이지 캐시 옵션.</summary>
public sealed record ResultPageCacheOptions
{
    /// <summary>메모리에 두는 페이지 추정 크기 합의 상한(기본 200 MB, TRD 4.4).</summary>
    public long MaxMemoryBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>밀려난 페이지를 쓸 임시 파일 폴더. null 이면 시스템 임시 폴더.</summary>
    public string? SpillDirectory { get; init; }
}

/// <summary>
/// 그리드 페이지 캐시(TRD 4.4). 소유: TSK-06-03.
/// 메모리 상한 안에서 LRU 로 유지하고, 밀려난 페이지는 임시 파일로 내렸다가 다시 부르면 읽어 온다
/// (커서는 앞으로만 가므로 <see cref="IResultSet"/> 에 같은 페이지를 다시 요청하지 않는다).
/// <see cref="IResultSet"/> 의 수명은 호출자 몫이다(이 캐시는 임시 파일만 정리한다).
/// </summary>
public sealed class ResultPageCache : IAsyncDisposable
{
    private readonly IResultSet _source;
    private readonly ResultPageCacheOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<int, Resident> _resident = [];
    private readonly LinkedList<int> _lru = new(); // 앞 = 가장 최근
    private readonly Dictionary<int, Spilled> _spilled = [];
    private readonly Dictionary<int, Task<ResultPage>> _loading = [];
    private readonly CancellationTokenSource _cts = new();
    private SafeFileHandle? _file;
    private long _fileLength;
    private long _residentBytes;
    private bool _disposed;

    public ResultPageCache(IResultSet source, ResultPageCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new ResultPageCacheOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxMemoryBytes, 1);
    }

    public IResultSet Source => _source;

    /// <summary>메모리에 있는 페이지의 추정 크기 합.</summary>
    public long ResidentBytes
    {
        get
        {
            lock (_gate)
            {
                return _residentBytes;
            }
        }
    }

    public int ResidentPageCount
    {
        get
        {
            lock (_gate)
            {
                return _resident.Count;
            }
        }
    }

    /// <summary>임시 파일로 내려간 적 있는 페이지 수.</summary>
    public int SpilledPageCount
    {
        get
        {
            lock (_gate)
            {
                return _spilled.Count;
            }
        }
    }

    /// <summary>메모리에 있으면 돌려주고 최근 사용으로 올린다. 없으면 false(비동기 가져오기를 시작하지 않는다).</summary>
    public bool TryGetResident(int pageIndex, out ResultPage page)
    {
        lock (_gate)
        {
            if (_resident.TryGetValue(pageIndex, out var r))
            {
                Touch(r);
                page = r.Page;
                return true;
            }
        }

        page = null!;
        return false;
    }

    /// <summary>페이지를 돌려준다. 메모리 → 임시 파일 → 원본 순. 같은 페이지의 동시 요청은 한 번만 가져온다.</summary>
    public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ct.ThrowIfCancellationRequested();
        Task<ResultPage> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resident.TryGetValue(pageIndex, out var r))
            {
                Touch(r);
                return Task.FromResult(r.Page);
            }

            if (!_loading.TryGetValue(pageIndex, out task!))
            {
                task = LoadAsync(pageIndex);
                _loading[pageIndex] = task;
            }
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _loading.Values];
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var t in pending)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // 정리 중 취소는 정상
            }
        }

        lock (_gate)
        {
            _file?.Dispose(); // DeleteOnClose
            _file = null;
            _resident.Clear();
            _lru.Clear();
            _spilled.Clear();
            _residentBytes = 0;
        }

        _cts.Dispose();
    }

    private async Task<ResultPage> LoadAsync(int pageIndex)
    {
        // 호출한 쪽이 일부 취소해도 다른 호출자를 위해 계속 진행한다(캐시 수명 토큰만 따른다).
        await Task.Yield();
        try
        {
            ResultPage page;
            Spilled? spilled;
            lock (_gate)
            {
                _spilled.TryGetValue(pageIndex, out spilled);
            }

            page = spilled is not null
                ? ReadSpilled(spilled)
                : await _source.GetPageAsync(pageIndex, _cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed)
                {
                    Admit(page);
                }
            }

            return page;
        }
        finally
        {
            lock (_gate)
            {
                _loading.Remove(pageIndex);
            }
        }
    }

    private void Touch(Resident r)
    {
        _lru.Remove(r.Node);
        _lru.AddFirst(r.Node);
    }

    private void Admit(ResultPage page)
    {
        var bytes = PageSerializer.EstimateBytes(page);
        var node = _lru.AddFirst(page.PageIndex);
        _resident[page.PageIndex] = new Resident(page, bytes, node);
        _residentBytes += bytes;

        while (_residentBytes > _options.MaxMemoryBytes && _resident.Count > 1)
        {
            var victimIndex = _lru.Last!.Value;
            var victim = _resident[victimIndex];
            if (!_spilled.ContainsKey(victimIndex))
            {
                _spilled[victimIndex] = WriteSpill(victim.Page);
            }

            _lru.RemoveLast();
            _resident.Remove(victimIndex);
            _residentBytes -= victim.Bytes;
        }
    }

    private SafeFileHandle EnsureFile()
    {
        if (_file is null)
        {
            var dir = _options.SpillDirectory ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "oranda-grid-" + Guid.NewGuid().ToString("N") + ".tmp");
            _file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
        }

        return _file;
    }

    private Spilled WriteSpill(ResultPage page)
    {
        var (bytes, side) = PageSerializer.Serialize(page);
        var handle = EnsureFile();
        RandomAccess.Write(handle, bytes, _fileLength);
        var rec = new Spilled(_fileLength, bytes.Length, side);
        _fileLength += bytes.Length;
        return rec;
    }

    private ResultPage ReadSpilled(Spilled s)
    {
        var buffer = new byte[s.Length];
        SafeFileHandle handle;
        lock (_gate)
        {
            handle = _file ?? throw new ObjectDisposedException(nameof(ResultPageCache));
        }

        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), s.Offset + read);
            if (n == 0)
            {
                throw new EndOfStreamException("임시 파일이 잘렸습니다.");
            }

            read += n;
        }

        return PageSerializer.Deserialize(buffer, s.Sidecar);
    }

    private sealed record Resident(ResultPage Page, long Bytes, LinkedListNode<int> Node);

    private sealed record Spilled(long Offset, int Length, Dictionary<(int Col, int Row), object> Sidecar);

    /// <summary>페이지 ↔ 바이트. 모르는 형식은 같은 프로세스 안에서만 되돌릴 수 있게 곁가지(sidecar)에 객체째 둔다.</summary>
    private static class PageSerializer
    {
        private const byte TNull = 0, TString = 1, TDecimal = 2, TLong = 3, TInt = 4, TDouble = 5, TFloat = 6, TBool = 7,
            TDateTime = 8, TDateTimeOffset = 9, TTimeSpan = 10, TBytes = 11, TGuid = 12, TShort = 13, TByte = 14,
            TFloatArray = 15, TDoubleArray = 16, TLob = 17, TSbyteArray = 18, TSide = 255;

        public static long EstimateBytes(ResultPage page)
        {
            long total = 64;
            foreach (var col in page.Columns)
            {
                total += 24 + 8L * col.Count;
                foreach (var v in col)
                {
                    total += v switch
                    {
                        null => 0,
                        string s => 24 + 2L * s.Length,
                        byte[] b => 24 + b.Length,
                        float[] f => 24 + 4L * f.Length,
                        double[] d => 24 + 8L * d.Length,
                        sbyte[] sb => 24 + sb.Length,
                        LobValue l => 96 + 2L * (l.TextPreview?.Length ?? 0) + l.BinaryPreview.Length,
                        decimal => 32,
                        _ => 24,
                    };
                }
            }

            return total;
        }

        public static (byte[] Bytes, Dictionary<(int Col, int Row), object> Sidecar) Serialize(ResultPage page)
        {
            var side = new Dictionary<(int, int), object>();
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(page.PageIndex);
                w.Write(page.FirstRowIndex);
                w.Write(page.RowCount);
                w.Write(page.IsLast);
                w.Write(page.Columns.Count);
                for (var c = 0; c < page.Columns.Count; c++)
                {
                    var col = page.Columns[c];
                    w.Write(col.Count);
                    for (var r = 0; r < col.Count; r++)
                    {
                        WriteValue(w, col[r], c, r, side);
                    }
                }
            }

            return (ms.ToArray(), side);
        }

        public static ResultPage Deserialize(byte[] bytes, Dictionary<(int Col, int Row), object> side)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var rd = new BinaryReader(ms, Encoding.UTF8);
            var pageIndex = rd.ReadInt32();
            var first = rd.ReadInt64();
            var rowCount = rd.ReadInt32();
            var isLast = rd.ReadBoolean();
            var cols = rd.ReadInt32();
            var columns = new List<IReadOnlyList<object?>>(cols);
            for (var c = 0; c < cols; c++)
            {
                var n = rd.ReadInt32();
                var col = new object?[n];
                for (var r = 0; r < n; r++)
                {
                    col[r] = ReadValue(rd, c, r, side);
                }

                columns.Add(col);
            }

            return new ResultPage(pageIndex, first, rowCount, columns) { IsLast = isLast };
        }

        private static void WriteValue(BinaryWriter w, object? v, int c, int r, Dictionary<(int, int), object> side)
        {
            switch (v)
            {
                case null or DBNull: w.Write(TNull); break;
                case string s: w.Write(TString); w.Write(s); break;
                case decimal d: w.Write(TDecimal); w.Write(d); break;
                case long l: w.Write(TLong); w.Write(l); break;
                case int i: w.Write(TInt); w.Write(i); break;
                case short sh: w.Write(TShort); w.Write(sh); break;
                case byte by: w.Write(TByte); w.Write(by); break;
                case double db: w.Write(TDouble); w.Write(db); break;
                case float f: w.Write(TFloat); w.Write(f); break;
                case bool b: w.Write(TBool); w.Write(b); break;
                case DateTime dt: w.Write(TDateTime); w.Write(dt.Ticks); w.Write((byte)dt.Kind); break;
                case DateTimeOffset dto: w.Write(TDateTimeOffset); w.Write(dto.Ticks); w.Write(dto.Offset.Ticks); break;
                case TimeSpan ts: w.Write(TTimeSpan); w.Write(ts.Ticks); break;
                case Guid g: w.Write(TGuid); w.Write(g.ToByteArray()); break;
                case byte[] bytes when bytes.GetType() == typeof(byte[]): w.Write(TBytes); w.Write(bytes.Length); w.Write(bytes); break;
                case sbyte[] sa:
                    w.Write(TSbyteArray);
                    w.Write(sa.Length);
                    foreach (var x in sa)
                    {
                        w.Write(x);
                    }

                    break;
                case float[] fa:
                    w.Write(TFloatArray);
                    w.Write(fa.Length);
                    foreach (var x in fa)
                    {
                        w.Write(x);
                    }

                    break;
                case double[] da:
                    w.Write(TDoubleArray);
                    w.Write(da.Length);
                    foreach (var x in da)
                    {
                        w.Write(x);
                    }

                    break;
                case LobValue lob:
                    w.Write(TLob);
                    w.Write((byte)lob.Kind);
                    w.Write(lob.Length);
                    w.Write(lob.IsTruncated);
                    w.Write(lob.TextPreview is not null);
                    if (lob.TextPreview is not null)
                    {
                        w.Write(lob.TextPreview);
                    }

                    w.Write(lob.BinaryPreview.Length);
                    w.Write(lob.BinaryPreview.Span);
                    if (lob.Locator is not null)
                    {
                        side[(c, r)] = lob.Locator; // 드라이버 불투명 값은 파일에 못 쓴다
                    }

                    break;
                default:
                    w.Write(TSide);
                    side[(c, r)] = v;
                    break;
            }
        }

        private static object? ReadValue(BinaryReader rd, int c, int r, Dictionary<(int Col, int Row), object> side)
        {
            var tag = rd.ReadByte();
            switch (tag)
            {
                case TNull: return null;
                case TString: return rd.ReadString();
                case TDecimal: return rd.ReadDecimal();
                case TLong: return rd.ReadInt64();
                case TInt: return rd.ReadInt32();
                case TShort: return rd.ReadInt16();
                case TByte: return rd.ReadByte();
                case TDouble: return rd.ReadDouble();
                case TFloat: return rd.ReadSingle();
                case TBool: return rd.ReadBoolean();
                case TDateTime: return new DateTime(rd.ReadInt64(), (DateTimeKind)rd.ReadByte());
                case TDateTimeOffset:
                    var ticks = rd.ReadInt64();
                    return new DateTimeOffset(ticks, new TimeSpan(rd.ReadInt64()));
                case TTimeSpan: return new TimeSpan(rd.ReadInt64());
                case TGuid: return new Guid(rd.ReadBytes(16));
                case TBytes: return rd.ReadBytes(rd.ReadInt32());
                case TFloatArray:
                    var fa = new float[rd.ReadInt32()];
                    for (var i = 0; i < fa.Length; i++)
                    {
                        fa[i] = rd.ReadSingle();
                    }

                    return fa;
                case TDoubleArray:
                    var da = new double[rd.ReadInt32()];
                    for (var i = 0; i < da.Length; i++)
                    {
                        da[i] = rd.ReadDouble();
                    }

                    return da;
                case TSbyteArray:
                    var sa = new sbyte[rd.ReadInt32()];
                    for (var i = 0; i < sa.Length; i++)
                    {
                        sa[i] = rd.ReadSByte();
                    }

                    return sa;
                case TLob:
                    var kind = (LobKind)rd.ReadByte();
                    var length = rd.ReadInt64();
                    var truncated = rd.ReadBoolean();
                    var text = rd.ReadBoolean() ? rd.ReadString() : null;
                    var bin = rd.ReadBytes(rd.ReadInt32());
                    side.TryGetValue((c, r), out var locator);
                    return new LobValue(kind, length) { TextPreview = text, BinaryPreview = bin, IsTruncated = truncated, Locator = locator };
                case TSide: return side[(c, r)];
                default: throw new InvalidDataException($"알 수 없는 값 태그 {tag}");
            }
        }
    }
}
