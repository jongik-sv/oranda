using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Completion;

/// <summary>
/// SQL 편집기의 자동완성 팝업(AvaloniaEdit <see cref="CompletionWindow"/>)과 인자 도움말(<see cref="OverloadInsightWindow"/>).
/// 후보는 <see cref="ICompletionProvider"/> 가 만든다(문맥 판정·메타데이터·150 ms 예산은 그쪽). 조회는 UI 스레드 밖에서 하고,
/// 결과가 왔을 때 사용자가 이미 다른 곳으로 커서를 옮겼으면 열지 않는다.
/// <para>
/// 서비스는 처음 쓸 때 앱 컨테이너에서 꺼낸다 — 시작 경로에서 Oranda.Oracle·Oranda.Language 를 올리지 않기 위해서다(D-006).
/// 접속이 없으면(<c>hasSession</c> 거짓) <c>.</c> 입력으로는 아무것도 하지 않아 지연 어셈블리 훑기도 일어나지 않는다.
/// </para>
/// UI 스레드에서만 쓴다.
/// </summary>
public sealed class CompletionController : IDisposable
{
    public static CompletionController Shared { get; } = new();

    /// <summary>후보 제공자(기본 = 앱 컨테이너의 <see cref="ICompletionProvider"/>). 시험이 바꾼다.</summary>
    public Func<ICompletionProvider?> ProviderResolver { get; set; } = () => Resolve<ICompletionProvider>();

    /// <summary>메타데이터 제공자(인자 도움말). 시험이 바꾼다.</summary>
    public Func<IMetadataProvider?> MetadataResolver { get; set; } = () => Resolve<IMetadataProvider>();

    /// <summary>편집기 탭의 세션(기본 = 탭 접속 프로필의 작업 세션 → 현재 접속 → 마지막 작업 세션). 시험이 바꾼다.</summary>
    public Func<SqlEditorViewModel?, ISession?> SessionResolver { get; set; } = DefaultSession;

    /// <summary><c>.</c> 입력 때 자동으로 연다.</summary>
    public bool AutoTriggerOnDot { get; set; } = true;

    /// <summary>테이블 이름 자리에서 스키마 없이 2자 이상 치고 잠시 멈추면 단축키 없이 목록을 연다.</summary>
    public bool AutoOpenOnTyping { get; set; } = true;

    /// <summary>마지막 입력 뒤 목록을 열기까지 기다리는 시간. 그 사이 더 치면 처음부터 다시 센다.</summary>
    public TimeSpan AutoOpenDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>일정 시간 뒤 동작을 한 번 실행하게 하고 취소 손잡이를 돌려준다(기본 = UI 스레드 타이머). 시험이 바꿔 실제로 기다리지 않는다.</summary>
    public Func<TimeSpan, Action, IDisposable> Schedule { get; set; } = static (delay, action) => DispatcherTimer.RunOnce(action, delay);

    /// <summary>문맥 판정기(기본 = 앱 컨테이너의 <see cref="ILanguageService"/>). 타이머가 울릴 때 처음 꺼낸다. 시험이 바꾼다.</summary>
    public Func<ILanguageService?> LanguageResolver { get; set; } = () => Resolve<ILanguageService>();

    public CompletionWindow? CurrentWindow { get; private set; }

    public OverloadInsightWindow? CurrentInsight { get; private set; }

    /// <summary>마지막으로 받은 후보(진단·시험용).</summary>
    public CompletionResult? LastResult { get; private set; }

    /// <summary>마지막 목록 창이 닫힐 때의 문서 버전.</summary>
    private ITextSourceVersion? _closedAtVersion;

    /// <summary>
    /// 방금 친 글자로 후보를 다시 물어야 하나(<see cref="CompletionResult.IsIncomplete"/>). 다 담지 못한 목록이 열려 있거나, 그 글자에 맞는 후보가 없어 방금 닫혔을 때다.
    /// 목록은 글자가 들어간 뒤 닫히므로 문서 버전이 그대로면 이 입력이 닫은 것이다. Esc 로 닫은 뒤 친 글자는 버전이 달라 다시 열지 않는다.
    /// </summary>
    public bool NeedsRefill(TextArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return LastResult is { IsIncomplete: true }
            && (CurrentWindow is not null || (_closedAtVersion is not null && ReferenceEquals(_closedAtVersion, area.Document?.Version)));
    }

    /// <summary>
    /// 커서 위치의 후보를 구해 목록 창을 연다. 열었으면 true.
    /// <paramref name="automatic"/> = <c>.</c> 입력으로 연 경우: 접속이 없으면 묻지 않고, 후보가 없으면 조용히 끝낸다.
    /// </summary>
    public Task<bool> ShowCompletionAsync(TextArea area, SqlEditorViewModel? tab, bool automatic, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        return ShowCoreAsync(area, tab, automatic, null, ct);
    }

    /// <summary>
    /// <paramref name="guard"/> = 입력 중 자동 열기(<see cref="ScheduleAutoOpen"/>)의 확인: 후보가 오는 사이 입력·커서·포커스가 바뀌었거나 다른 목록이 열렸으면 버리고,
    /// 후보가 없거나 입력한 글자와 똑같은 이름 하나뿐이면 열지 않는다(그때 Enter 는 줄바꿈이어야 한다).
    /// </summary>
    private async Task<bool> ShowCoreAsync(TextArea area, SqlEditorViewModel? tab, bool automatic, AutoOpenGuard? guard, CancellationToken ct)
    {
        var session = SessionResolver(tab);
        if (automatic && session is null)
        {
            return false;
        }

        var provider = ProviderResolver();
        var doc = area.Document;
        if (provider is null || doc is null)
        {
            return false;
        }

        var text = doc.Text;
        int caret = area.Caret.Offset;
        CompletionResult result;
        try
        {
            result = await Task.Run(() => provider.GetCompletionsAsync(session, text, caret, ct), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (guard is not null)
        {
            if (!guard.IsFresh(this) || result.Items.Count == 0 || IsOnlyTheTypedName(result, doc, caret))
            {
                return false;
            }
        }

        LastResult = result;
        if (result.Items.Count == 0 || !ReferenceEquals(area.Document, doc))
        {
            return false;
        }

        // 기다리는 사이 사용자가 더 쳤으면, 바꿀 자리부터 커서까지가 이름 글자일 때만 연다(목록이 그 글자로 걸러진다).
        int start = result.ReplaceRange.Start;
        int now = area.Caret.Offset;
        if (now < start || now > doc.TextLength || !IsIdentifier(doc.GetText(start, now - start)))
        {
            return false;
        }

        CloseCompletion();
        EnsureEditorTheme(area);
        // 기본 폭(175)은 이름만 겨우 들어간다. 아이콘·이름·상세(열 형식) 세 칸이 보이게 넓힌다.
        var window = new CompletionWindow(area) { StartOffset = start, EndOffset = now, Width = 360 };
        var list = window.CompletionList.CompletionData;
        foreach (var item in result.Items)
        {
            list.Add(new CompletionData(item));
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(CurrentWindow, window))
            {
                CurrentWindow = null;
                _closedAtVersion = doc.Version;
            }
        };
        CurrentWindow = window;
        window.Show();
        window.CompletionList.ApplyTemplate();
        var typed = doc.GetText(start, now - start);
        if (typed.Length > 0 && window.CompletionList.ListBox is not null)
        {
            window.CompletionList.SelectItem(typed);
        }
        else if (list.Count > 0)
        {
            window.CompletionList.SelectedItem = list[0]; // SQL Developer 처럼 첫 후보를 골라 두어 Enter·Tab 으로 바로 넣는다
        }

        return true;
    }

    private static bool IsOnlyTheTypedName(CompletionResult result, TextDocument doc, int caret)
    {
        int start = result.ReplaceRange.Start;
        return result.Items.Count == 1 && start >= 0 && start <= caret && caret <= doc.TextLength
            && string.Equals(result.Items[0].Label, doc.GetText(start, caret - start), StringComparison.OrdinalIgnoreCase);
    }

    // ---- 입력 중 자동 열기 -----------------------------------------------------------------------

    private CancellationTokenSource? _autoCts;
    private IDisposable? _autoTimer;
    private TextArea? _autoArea;
    private int _autoGeneration;
    private WeakReference<TextDocument>? _escapedDoc;
    private TextAnchor? _escapedAnchor;
    private readonly ConditionalWeakTable<TextArea, object> _hooked = [];

    /// <summary>기다리는 자동 열기가 있나(시험용).</summary>
    public bool HasPendingAutoOpen => _autoTimer is not null;

    /// <summary>
    /// 이름 글자가 하나 들어갔다: 마지막 입력 뒤 <see cref="AutoOpenDelay"/> 가 지나면 후보를 물을지 정한다.
    /// 여기서는 값싼 조건만 본다 — 열린 목록이 있거나 다시 채울 목록이면(<see cref="NeedsRefill"/>) 그쪽이 맡고, 확정한 이름 글자가 2자 미만이거나
    /// 스키마를 쓰는 중이거나 Esc 로 닫은 단어면 걸지 않는다. 접속·문맥 판정은 타이머가 울릴 때 본다.
    /// <paramref name="blocked"/> = 읽기 전용·추가 커서·열 블록처럼 편집기가 알려 주는 막힘(걸 때와 울릴 때 모두 본다).
    /// </summary>
    public void ScheduleAutoOpen(TextArea area, SqlEditorViewModel? tab, Func<bool> blocked)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(blocked);
        CancelAutoOpen();
        var doc = area.Document;
        if (!AutoOpenOnTyping || doc is null || CurrentWindow is not null || NeedsRefill(area) || blocked())
        {
            return;
        }

        int caret = area.Caret.Offset;
        int start = caret;
        while (start > 0 && IsNameChar(doc.GetCharAt(start - 1)))
        {
            start--;
        }

        if (caret - start < 2 || (start > 0 && doc.GetCharAt(start - 1) == '.') || IsEscapedWord(doc, start))
        {
            return;
        }

        if (SessionResolver(tab) is null)
        {
            return;
        }

        Hook(area);
        var cts = _autoCts = new CancellationTokenSource();
        var guard = new AutoOpenGuard(area, doc, doc.Version, caret, blocked, ++_autoGeneration);
        _autoArea = area;
        _autoTimer = Schedule(AutoOpenDelay, () => _ = RunAutoOpenAsync(area, tab, guard, cts.Token));
    }

    public void Dispose() => CancelAutoOpen();

    /// <summary>기다리던 자동 열기와 진행 중인 조회를 버린다. 글자가 아닌 입력·포커스 이동·편집기 제거에서 부른다.</summary>
    public void CancelAutoOpen()
    {
        _autoGeneration++;
        _autoTimer?.Dispose();
        _autoTimer = null;
        _autoArea = null;
        var cts = _autoCts;
        _autoCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>목록이 열린 채 Esc 를 눌렀다: 이 단어를 계속 치는 동안은 다시 열지 않는다(다음 단어에서는 다시 열린다).</summary>
    public void NoteEscape(TextArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (CurrentWindow is not { } window || area.Document is not { } doc)
        {
            return;
        }

        _escapedDoc = new WeakReference<TextDocument>(doc);
        _escapedAnchor = doc.CreateAnchor(Math.Clamp(window.StartOffset, 0, doc.TextLength));
        _escapedAnchor.MovementType = AnchorMovementType.BeforeInsertion;
    }

    private bool IsEscapedWord(TextDocument doc, int wordStart)
    {
        if (_escapedAnchor is not { IsDeleted: false } anchor || _escapedDoc is null || !_escapedDoc.TryGetTarget(out var escaped) || !ReferenceEquals(escaped, doc))
        {
            _escapedAnchor = null;
            _escapedDoc = null;
            return false;
        }

        return anchor.Offset == wordStart;
    }

    /// <summary>편집기가 사라지거나 포커스를 잃으면 기다리던 자동 열기를 버린다(탭을 닫아도 타이머가 남지 않는다).</summary>
    private void Hook(TextArea area)
    {
        if (_hooked.TryGetValue(area, out _))
        {
            return;
        }

        _hooked.Add(area, area);
        area.DetachedFromVisualTree += (_, _) => CancelFor(area);
        area.LostFocus += (_, _) => CancelFor(area);
    }

    private void CancelFor(TextArea area)
    {
        if (ReferenceEquals(_autoArea, area))
        {
            CancelAutoOpen();
        }
    }

    private async Task RunAutoOpenAsync(TextArea area, SqlEditorViewModel? tab, AutoOpenGuard guard, CancellationToken ct)
    {
        try
        {
            _autoTimer = null;
            if (!guard.IsFresh(this) || LanguageResolver() is not { } language)
            {
                return;
            }

            var text = guard.Doc.Text;
            var context = await Task.Run(() => language.GetCompletionContext(text, guard.Caret), ct).ConfigureAwait(true);
            if (context is { Kind: CompletionContextKind.TableOrView, Qualifier: null } && context.Prefix.Length >= 2 && guard.IsFresh(this))
            {
                await ShowCoreAsync(area, tab, automatic: true, guard, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 새 입력이나 포커스 이동이 버렸다.
        }
        finally
        {
            if (_autoGeneration == guard.Generation)
            {
                _autoArea = null; // 끝난 자동 열기가 편집기를 붙들고 있지 않게 한다
            }
        }
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>자동 열기를 건 순간의 모습. 후보를 기다린 뒤에도 같아야 연다.</summary>
    private sealed record AutoOpenGuard(TextArea Area, TextDocument Doc, ITextSourceVersion Version, int Caret, Func<bool> Blocked, int Generation)
    {
        public bool IsFresh(CompletionController c)
            => c._autoGeneration == Generation
                && ReferenceEquals(Area.Document, Doc)
                && ReferenceEquals(Doc.Version, Version)
                && Area.Caret.Offset == Caret
                && Area.IsKeyboardFocusWithin
                && Area.IsEffectivelyVisible
                && c.CurrentWindow is null
                && !Blocked();
    }

    /// <summary>커서를 감싼 호출의 시그니처 창을 연다. 열었으면 true.</summary>
    public async Task<bool> ShowParameterHintsAsync(TextArea area, SqlEditorViewModel? tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        var session = SessionResolver(tab);
        var meta = session is null ? null : MetadataResolver();
        if (session is null || meta is null || area.Document is null)
        {
            return false;
        }

        var signatures = await FindSignaturesAsync(meta, session, area.Document.Text, area.Caret.Offset, ct).ConfigureAwait(true);
        if (signatures is null)
        {
            return false;
        }

        CloseInsight();
        EnsureEditorTheme(area);
        var window = new OverloadInsightWindow(area) { Provider = signatures };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(CurrentInsight, window))
            {
                CurrentInsight = null;
            }
        };
        CurrentInsight = window;
        window.Show();
        return true;
    }

    private static async Task<SignatureOverloads?> FindSignaturesAsync(IMetadataProvider meta, ISession session, string text, int caret, CancellationToken ct)
    {
        var call = global::Oranda.Language.Completion.ParameterHints.FindCallSite(text, caret);
        if (call is null)
        {
            return null;
        }

        try
        {
            var list = await Task.Run(() => global::Oranda.Language.Completion.ParameterHints.GetSignaturesAsync(meta, session, call, ct), ct).ConfigureAwait(true);
            return list.Count == 0 ? null : new SignatureOverloads(list, call.ArgumentIndex);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void CloseCompletion()
    {
        CurrentWindow?.Close();
        CurrentWindow = null;
    }

    public void CloseInsight()
    {
        CurrentInsight?.Close();
        CurrentInsight = null;
    }

    private static readonly ConditionalWeakTable<TopLevel, StyleInclude> s_themed = [];

    /// <summary>
    /// AvaloniaEdit 팝업(목록·도움말)의 논리 부모는 편집기 View 가 아니라 창(TopLevel)이라 View 에 건 AvaloniaEdit 테마가 닿지 않는다.
    /// 팝업을 만들기 전에 창에 테마를 한 번 건다(창마다 한 번, 앱 전역 스타일 파일은 건드리지 않는다).
    /// </summary>
    private static void EnsureEditorTheme(TextArea area)
    {
        if (TopLevel.GetTopLevel(area) is not { } top || s_themed.TryGetValue(top, out _))
        {
            return;
        }

        var theme = new StyleInclude(new Uri("avares://Oranda.App/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        };
        top.Styles.Add(theme);
        s_themed.Add(top, theme);
    }

    private static bool IsIdentifier(string s)
    {
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$' or '#'))
            {
                return false;
            }
        }

        return true;
    }

    // ---- 서비스 ------------------------------------------------------------------------------

    private static ServiceContainer? Services => (Application.Current as App)?.Services;

    private static T? Resolve<T>()
        where T : class => Services?.GetService<T>();

    private static ISession? DefaultSession(SqlEditorViewModel? tab)
    {
        if (Services is not { } sp || sp.GetService<IContextKeys>() is not { } keys || !keys.Evaluate("hasSession"))
        {
            return null; // 접속이 없으면 세션 관리자(Oracle 어셈블리)를 꺼내지 않는다
        }

        // 탭의 세션(전용 세션·묶인 접속, 묶이지 않았으면 지금 접속). 끊긴 세션에는 묻지 않는다.
        return SessionRouter.ForTab(sp, tab) is { State: not (SessionState.Broken or SessionState.Closed) } session
            && sp.GetService<IConnectionService>()?.IsSessionBroken(session.Id) != true
                ? session
                : null;
    }
}
