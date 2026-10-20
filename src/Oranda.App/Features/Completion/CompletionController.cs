using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit.CodeCompletion;
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
public sealed class CompletionController
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

    public CompletionWindow? CurrentWindow { get; private set; }

    public OverloadInsightWindow? CurrentInsight { get; private set; }

    /// <summary>마지막으로 받은 후보(진단·시험용).</summary>
    public CompletionResult? LastResult { get; private set; }

    /// <summary>
    /// 커서 위치의 후보를 구해 목록 창을 연다. 열었으면 true.
    /// <paramref name="automatic"/> = <c>.</c> 입력으로 연 경우: 접속이 없으면 묻지 않고, 후보가 없으면 조용히 끝낸다.
    /// </summary>
    public async Task<bool> ShowCompletionAsync(TextArea area, SqlEditorViewModel? tab, bool automatic, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
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
