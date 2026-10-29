using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 표의 칸 폭(DIP). 키는 PlanView.axaml 의 칸 클래스 이름(<c>c-id</c> … <c>c-ashare</c>)과 같다.
/// 폭을 바꾸면 <see cref="Changed"/> 로 알리고, 화면은 그 값을 리소스로 옮겨 머리글과 모든 줄이 함께 바뀐다.
/// 기본값과 다른 폭만 앱 데이터 폴더의 <c>plan-columns.json</c> 에 남는다(<c>recent-files.json</c> 과 같은 관례: 손상·IO 오류 = 기본 폭).
/// UI 스레드에서 쓴다.
/// </summary>
public sealed class PlanColumnWidths
{
    /// <summary>저장 파일 이름.</summary>
    public const string FileName = "plan-columns.json";

    /// <summary>칸 키와 (기본 폭, 최소 폭). 표시 순서와 같다.</summary>
    private static readonly (string Key, double Default, double Min)[] Specs =
    [
        ("c-id", 34, 24),
        ("c-order", 48, 32),
        ("c-op", 290, 80),
        ("c-obj", 150, 50),
        ("c-risk", 96, 40),
        ("c-cost", 66, 40),
        ("c-share", 100, 72),
        ("c-erows", 94, 44),
        ("c-arows", 94, 44),
        ("c-starts", 62, 40),
        ("c-time", 66, 40),
        ("c-ashare", 100, 72),
    ];

    /// <summary>폭의 위쪽 한계(실수로 화면 밖까지 끄는 것을 막는다).</summary>
    public const double MaxWidth = 1200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, double> _widths = Specs.ToDictionary(s => s.Key, s => s.Default);
    private readonly string? _file;

    /// <summary>메모리만 쓴다(저장 안 함).</summary>
    public PlanColumnWidths()
    {
    }

    /// <summary><paramref name="dataDirectory"/> 의 <c>plan-columns.json</c> 에서 폭을 읽고, <see cref="Save"/> 로 쓴다.</summary>
    public PlanColumnWidths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _file = Path.Combine(dataDirectory, FileName);
        Load();
    }

    /// <summary>폭이 바뀐 칸의 키.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>모든 칸 키(표시 순서).</summary>
    public static IReadOnlyList<string> Keys { get; } = Specs.Select(s => s.Key).ToArray();

    public static double DefaultOf(string key) => Spec(key).Default;

    public static double MinOf(string key) => Spec(key).Min;

    public double Get(string key) => _widths[Spec(key).Key];

    /// <summary>폭을 정한다(최소·최대로 맞춘다). 값이 바뀌면 <see cref="Changed"/> 를 일으킨다.</summary>
    public void Set(string key, double width)
    {
        var spec = Spec(key);
        var value = double.IsFinite(width) ? Math.Round(Math.Clamp(width, spec.Min, MaxWidth)) : spec.Default;
        if (_widths[key] == value)
        {
            return;
        }

        _widths[key] = value;
        Changed?.Invoke(this, key);
    }

    /// <summary>기본 폭으로 되돌린다.</summary>
    public void Reset(string key) => Set(key, Spec(key).Default);

    /// <summary>기본 폭과 다른 칸만 파일에 쓴다(없으면 파일을 지우지 않고 빈 목록으로 쓴다). 실패는 삼킨다.</summary>
    public void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var changed = Specs.Where(s => _widths[s.Key] != s.Default).ToDictionary(s => s.Key, s => _widths[s.Key]);
            File.WriteAllText(_file, JsonSerializer.Serialize(changed, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 폭 저장 실패는 화면 동작을 막지 않는다.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_file));
            foreach (var (key, width) in saved ?? [])
            {
                if (_widths.ContainsKey(key) && double.IsFinite(width))
                {
                    var spec = Spec(key);
                    _widths[key] = Math.Round(Math.Clamp(width, spec.Min, MaxWidth));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 손상·읽기 실패 = 기본 폭.
        }
    }

    private static (string Key, double Default, double Min) Spec(string key) =>
        Array.Find(Specs, s => s.Key == key) is { Key: not null } spec
            ? spec
            : throw new ArgumentException($"알 수 없는 칸 키: {key}", nameof(key));
}
