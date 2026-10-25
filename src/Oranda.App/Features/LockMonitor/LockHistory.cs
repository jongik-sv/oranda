using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Oranda.App.Features.LockMonitor;

/// <summary>이력 한 점(LK-10): 새로 고침 한 번의 대기자 수와 가장 오래 기다린 초.</summary>
public sealed record LockHistoryPoint(DateTimeOffset At, int Waiters, long MaxWaitSeconds);

/// <summary>
/// Lock 모니터 이력(LK-10). 새로 고침마다 한 점을 앱 메모리에 쌓고, 가장 새 점에서 <see cref="Retention"/> 보다 오래된 점은 버린다.
/// ASH 를 쓰지 않는다(Diagnostics Pack). 접속(프로필)마다 하나를 <see cref="For"/> 로 나눠 써서 창을 닫았다 열어도 남는다.
/// </summary>
public sealed class LockHistoryBuffer
{
    /// <summary>점 개수 상한(보관 12 시간·2 초 간격도 넘지 않게).</summary>
    public const int MaxPoints = 25_000;

    public static readonly TimeSpan MinRetention = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<Guid, LockHistoryBuffer> Shared = new();

    private readonly List<LockHistoryPoint> _points = [];
    private readonly object _gate = new();
    private TimeSpan _retention;

    public LockHistoryBuffer(TimeSpan retention)
    {
        _retention = retention < MinRetention ? MinRetention : retention;
    }

    /// <summary>보관 길이. 줄이면 바로 오래된 점을 버린다.</summary>
    public TimeSpan Retention
    {
        get
        {
            lock (_gate)
            {
                return _retention;
            }
        }

        set
        {
            lock (_gate)
            {
                _retention = value < MinRetention ? MinRetention : value;
                TrimLocked();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>시각 순 복사본.</summary>
    public IReadOnlyList<LockHistoryPoint> Points
    {
        get
        {
            lock (_gate)
            {
                return [.. _points];
            }
        }
    }

    /// <summary>대기자가 가장 많았던 점(같으면 먼저 온 점). 점이 없으면 null.</summary>
    public LockHistoryPoint? Peak
    {
        get
        {
            lock (_gate)
            {
                LockHistoryPoint? best = null;
                foreach (var p in _points)
                {
                    if (best is null || p.Waiters > best.Waiters)
                    {
                        best = p;
                    }
                }

                return best;
            }
        }
    }

    /// <summary>프로필마다 하나. 처음이면 <paramref name="retention"/> 으로 만들고, 있으면 보관 길이만 맞춘다.</summary>
    public static LockHistoryBuffer For(Guid profileId, TimeSpan retention)
    {
        var buffer = Shared.GetOrAdd(profileId, _ => new LockHistoryBuffer(retention));
        buffer.Retention = retention;
        return buffer;
    }

    /// <summary>점을 더한다. 시각이 앞선 점(시계가 되돌아감)은 제자리에 끼운다. 음수는 0 으로.</summary>
    public void Add(LockHistoryPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        point = point with { Waiters = Math.Max(0, point.Waiters), MaxWaitSeconds = Math.Max(0, point.MaxWaitSeconds) };
        lock (_gate)
        {
            var at = _points.Count;
            while (at > 0 && _points[at - 1].At > point.At)
            {
                at--;
            }

            _points.Insert(at, point);
            TrimLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _points.Clear();
        }
    }

    private void TrimLocked()
    {
        if (_points.Count == 0)
        {
            return;
        }

        var cut = _points[^1].At - _retention;
        var drop = 0;
        while (drop < _points.Count && _points[drop].At < cut)
        {
            drop++;
        }

        drop = Math.Max(drop, _points.Count - MaxPoints);
        if (drop > 0)
        {
            _points.RemoveRange(0, drop);
        }
    }
}

/// <summary>
/// 앱 데이터 폴더 <c>lock-history/</c> 에 쓰는 로컬 파일(DB 쓰기 아님): 이력 CSV 와 임계치 스냅샷 텍스트.
/// </summary>
public static class LockHistoryFiles
{
    public const string FolderName = "lock-history";

    // Windows 가 못 쓰는 글자. macOS·Linux 의 GetInvalidFileNameChars 는 '/' 와 NUL 뿐이라, 같은 접속이 OS 마다 다른 파일 이름이 되지 않게 함께 막는다.
    private static readonly char[] PortableInvalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>파일 이름에 쓸 수 없는 글자(어느 OS 에서든)를 밑줄로.</summary>
    public static string SafeName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string([.. (string.IsNullOrWhiteSpace(name) ? "lock" : name.Trim())
            .Select(c => invalid.Contains(c) || PortableInvalid.Contains(c) || char.IsControl(c) || c is '@' or ' ' ? '_' : c)]);
        return text.Length > 60 ? text[..60] : text;
    }

    /// <summary>이력 CSV 경로: <c>history-접속-yyyyMMdd.csv</c>(그 점의 현지 날짜).</summary>
    public static string HistoryPath(string folder, string account, DateTimeOffset at) =>
        Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"history-{SafeName(account)}-{at.ToLocalTime():yyyyMMdd}.csv"));

    /// <summary>이력 한 줄을 덧붙인다. 새 파일이면 머리글부터. 돌려준 값 = 쓴 파일.</summary>
    public static string AppendHistory(string folder, string account, LockHistoryPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        Directory.CreateDirectory(folder);
        var path = HistoryPath(folder, account, point.At);
        var line = string.Create(CultureInfo.InvariantCulture, $"{point.At.ToLocalTime():yyyy-MM-dd HH:mm:ss},{point.Waiters},{point.MaxWaitSeconds}\n");
        if (!File.Exists(path))
        {
            line = "at,waiters,max_wait_seconds\n" + line;
        }

        File.AppendAllText(path, line, new UTF8Encoding(false));
        return path;
    }

    /// <summary>스냅샷 텍스트를 <c>snapshot-yyyyMMdd-HHmmss-인스턴스_SID.txt</c> 로 쓴다(같은 이름이 있으면 뒤에 번호).</summary>
    public static string WriteSnapshot(string folder, LockAlert alert, string text)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Directory.CreateDirectory(folder);
        var stem = string.Create(CultureInfo.InvariantCulture,
            $"snapshot-{alert.At.ToLocalTime():yyyyMMdd-HHmmss}-{alert.Holder.InstanceId}_{alert.Holder.Sid}");
        var path = Path.Combine(folder, stem + ".txt");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{i}.txt"));
        }

        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }
}
