using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;

namespace Oranda.App.Features.Localization;

/// <summary>
/// XAML 에 한국어로 바로 적힌 화면 글자(제목·단추·머리글·힌트·도움말·낭독기 이름)를 영어로 바꿔 보여 준다.
/// 각 기능 Task 의 화면 파일을 고치지 않고 한/영을 맞추기 위한 길이다: 컨트롤이 화면에 올라올 때(<c>Loaded</c>)
/// 값이 <c>Resources/literals-en.json</c> 에 있는 한국어 글이면 영어로 바꾸고, 한국어로 돌아오면 원래 글로 되돌린다.
/// 바인딩으로 들어오는 글(ViewModel 이 만든 문자열)은 대상이 아니다 — 그쪽은 <see cref="IShellText"/> 를 써야 한다.
/// 한국어가 켜져 있을 때는 아무것도 하지 않는다(시작 경로 비용 없음).
/// </summary>
public static class LiteralTranslator
{
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);
    private static readonly object Gate = new();
    private static readonly List<WeakReference<TopLevel>> Windows = [];
    private static readonly List<WeakReference<Control>> Tracked = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Dictionary<AvaloniaProperty, Entry>> Applied = new();
    private static readonly List<WeakReference<Control>> Relayout = [];
    private static IShellText? _text;
    private static EventHandler? _handler;
    private static int _registered;

    private sealed record Entry(string Original, string Translated);

    /// <summary>지금 쓰는 언어(<c>ko</c> | <c>en</c>).</summary>
    public static string Language => _text?.Language ?? "ko";

    /// <summary>번역 표(한국어 글 → 영어 글). 시험이 검사한다.</summary>
    public static IReadOnlyDictionary<string, string> Table => English.Value;

    /// <summary>셸 글자 조회에 묶는다. 앱당 하나이며, 다시 부르면 앞의 연결은 끊긴다.</summary>
    public static void Install(IShellText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (Gate)
        {
            if (_text is not null && _handler is not null)
            {
                _text.LanguageChanged -= _handler;
            }

            _text = text;
            _handler = (_, _) => Refresh();
            text.LanguageChanged += _handler;
        }

        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Control.LoadedEvent.AddClassHandler<Control>(
                (c, e) =>
                {
                    if (c is TopLevel top)
                    {
                        lock (Gate)
                        {
                            Windows.RemoveAll(w => !w.TryGetTarget(out var alive));
                            if (!Windows.Exists(w => w.TryGetTarget(out var t) && ReferenceEquals(t, top)))
                            {
                                Windows.Add(new WeakReference<TopLevel>(top));
                            }
                        }
                    }

                    Translate(c);
                },
                RoutingStrategies.Direct);
        }
    }

    /// <summary>번역한 컨트롤을 모두 지금 언어에 맞춘다(언어가 바뀔 때).</summary>
    public static void Refresh()
    {
        List<Control> alive = [];
        lock (Gate)
        {
            Tracked.RemoveAll(w =>
            {
                if (w.TryGetTarget(out var c))
                {
                    alive.Add(c);
                    return false;
                }

                return true;
            });
        }

        List<TopLevel> windows = [];
        lock (Gate)
        {
            foreach (var w in Windows)
            {
                if (w.TryGetTarget(out var t))
                {
                    windows.Add(t);
                }
            }
        }

        // 열려 있는 창 안의 컨트롤(한국어일 때는 번역한 적이 없어 추적 목록에 없다)과 번역해 둔 컨트롤 모두.
        foreach (var top in windows)
        {
            if (top is Control root)
            {
                Translate(root);
            }

            foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(top))
            {
                if (v is Control child)
                {
                    Translate(child);
                }
            }
        }

        foreach (var c in alive)
        {
            Translate(c);
        }
    }

    /// <summary>컨트롤 하나의 글자를 지금 언어에 맞춘다.</summary>
    public static void Translate(Control c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var toEnglish = Language == "en";
        if (!toEnglish && !Applied.TryGetValue(c, out _))
        {
            return;
        }

        foreach (var p in Properties(c))
        {
            Sync(c, p, toEnglish);
        }
    }

    private static IEnumerable<AvaloniaProperty> Properties(Control c)
    {
        if (c is TextBlock)
        {
            yield return TextBlock.TextProperty;
        }

        if (c is ContentControl)
        {
            yield return ContentControl.ContentProperty;
        }

        if (c is HeaderedContentControl)
        {
            yield return HeaderedContentControl.HeaderProperty;
        }

        if (c is HeaderedItemsControl)
        {
            yield return HeaderedItemsControl.HeaderProperty;
        }

        // 오른쪽 클릭 메뉴 항목(MenuItem)은 HeaderedSelectingItemsControl 이다.
        if (c is HeaderedSelectingItemsControl)
        {
            yield return HeaderedSelectingItemsControl.HeaderProperty;
        }

        if (c is TextBox)
        {
            yield return TextBox.PlaceholderTextProperty;
        }

        if (c is NumericUpDown)
        {
            yield return NumericUpDown.FormatStringProperty;
        }

        if (c is Window)
        {
            yield return Window.TitleProperty;
        }

        yield return AutomationProperties.NameProperty;
        yield return ToolTip.TipProperty;
    }

    private static void Sync(Control c, AvaloniaProperty property, bool toEnglish)
    {
        var current = c.GetValue(property) as string;
        Applied.TryGetValue(c, out var map);
        if (toEnglish)
        {
            if (current is not null && map?.TryGetValue(property, out var done) == true && current == done.Translated)
            {
                return; // 이미 영어
            }

            if (current is not null && English.Value.TryGetValue(current, out var en))
            {
                map ??= Applied.GetOrCreateValue(c);
                map[property] = new Entry(current, en);
                c.SetValue(property, en);
                ScheduleRelayout(c);
                lock (Gate)
                {
                    if (!Tracked.Exists(w => w.TryGetTarget(out var t) && ReferenceEquals(t, c)))
                    {
                        Tracked.Add(new WeakReference<Control>(c));
                    }
                }
            }

            return;
        }

        // 한국어로 돌아간다: 우리가 바꿔 둔 값일 때만 원래 글로 되돌린다(그 사이 다른 값이 들어왔으면 건드리지 않는다).
        if (map is not null && map.TryGetValue(property, out var entry))
        {
            if (current == entry.Translated)
            {
                c.SetValue(property, entry.Original);
                ScheduleRelayout(c);
            }

            map.Remove(property);
        }
    }

    /// <summary>
    /// 글자를 바꾼 컨트롤과 그 조상을 다음 차례에 다시 재게 한다. 창이 처음 뜰 때(Loaded) 바꾼 글은 그 자리의 측정 무효화가
    /// 실제 창에서 사라질 때가 있어(대화상자 툴바 단추가 한국어 폭으로 남아 「New co」로 잘림) 배치가 끝난 뒤 한 번 더 건다.
    /// </summary>
    private static void ScheduleRelayout(Control c)
    {
        bool first;
        lock (Gate)
        {
            first = Relayout.Count == 0;
            Relayout.Add(new WeakReference<Control>(c));
        }

        if (first)
        {
            Dispatcher.UIThread.Post(FlushRelayout, DispatcherPriority.Background);
        }
    }

    private static void FlushRelayout()
    {
        List<Control> controls = [];
        lock (Gate)
        {
            foreach (var w in Relayout)
            {
                if (w.TryGetTarget(out var c))
                {
                    controls.Add(c);
                }
            }

            Relayout.Clear();
        }

        foreach (var c in controls)
        {
            for (Visual? v = c; v is not null; v = v.GetVisualParent())
            {
                (v as Layoutable)?.InvalidateMeasure();
            }
        }
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = typeof(LiteralTranslator).Assembly.GetManifestResourceStream("Oranda.App.literals-en.json");
        if (stream is null)
        {
            return table;
        }

        using var doc = JsonDocument.Parse(stream);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.GetString() is { } text)
            {
                table[p.Name] = text;
            }
        }

        return table;
    }
}
