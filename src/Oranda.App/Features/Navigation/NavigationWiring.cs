using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// Ctrl+클릭(macOS 는 Cmd+클릭) 정의로 이동을 SQL 편집기 탭에 붙인다. 편집기 파일(TSK-04-02)은 건드리지 않고,
/// 탭이 열릴 때 View 가 붙기를 기다려(짧게 다시 확인) 편집 영역에 마우스 처리기를 단다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class NavigationWiring : IStartupTask
{
    private static readonly ConditionalWeakTable<SqlEditorView, object> Attached = [];

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() => Install(SqlEditorHost.Current));
        return Task.CompletedTask;
    }

    /// <summary>호스트의 지금·앞으로 열릴 탭에 Ctrl+클릭을 단다(UI 스레드).</summary>
    public static void Install(SqlEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (var tab in host.Tabs.ToList())
        {
            WhenViewReady(tab, 0);
        }

        host.TabOpened += (_, tab) => WhenViewReady(tab, 0);
    }

    private static void WhenViewReady(SqlEditorViewModel tab, int attempt)
    {
        if (tab.Surface is SqlEditorView view)
        {
            Attach(view, tab);
            return;
        }

        if (attempt >= 20)
        {
            return;
        }

        DispatcherTimer.RunOnce(() => WhenViewReady(tab, attempt + 1), TimeSpan.FromMilliseconds(150));
    }

    /// <summary>편집기 View 에 Ctrl+클릭 처리기를 단다(한 View 에 한 번만).</summary>
    public static void Attach(SqlEditorView view, SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(tab);
        if (!Attached.TryAdd(view, tab))
        {
            return;
        }

        var area = view.TextEditorControl.TextArea;
        area.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnPressed(view, tab, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private static void OnPressed(SqlEditorView view, SqlEditorViewModel tab, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(view).Properties;
        if (!props.IsLeftButtonPressed || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0)
        {
            return;
        }

        var offset = OffsetAt(view, e);
        if (offset is null)
        {
            return;
        }

        e.Handled = true;
        _ = GoAsync(tab, offset.Value);
    }

    internal static int? OffsetAt(SqlEditorView view, PointerEventArgs e)
    {
        var tv = view.TextEditorControl.TextArea.TextView;
        var pos = tv.GetPosition(e.GetPosition(tv) + tv.ScrollOffset);
        return pos is null ? null : view.TextEditorControl.Document.GetOffset(pos.Value.Location);
    }

    private static async Task GoAsync(SqlEditorViewModel tab, int offset)
    {
        try
        {
            await NavigationService.Current.GoToDefinitionAsync(tab, offset).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
