using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Language;
using Oranda.Language;
using Oranda.Language.Lint;

namespace Oranda.App.Tests.Features.Diagnostics;

/// <summary>TSK-13-04 편집기 쪽: 점검(lint) 경고 밑줄이 문법 분석과 별도 단계로 붙고, 설정으로 끄면 사라진다.</summary>
public sealed class LintDiagnosticsTests
{
    private static readonly ILanguageService Language = new LanguageService();

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static async Task<T> WithLint<T>(bool enabled, Func<Task<T>> body)
    {
        var oldService = EditorDiagnostics.LintServiceProvider;
        var oldEnabled = EditorDiagnostics.LintEnabledProvider;
        EditorDiagnostics.LintServiceProvider = () => new LintService(lookup: null);
        EditorDiagnostics.LintEnabledProvider = () => enabled;
        try
        {
            return await body().ConfigureAwait(true);
        }
        finally
        {
            EditorDiagnostics.LintServiceProvider = oldService;
            EditorDiagnostics.LintEnabledProvider = oldEnabled;
        }
    }

    [Fact]
    public Task Lint_warning_is_underlined_and_survives_syntax_pass() => Ui.Run(() => WithLint(true, async () =>
    {
        var (window, view, vm) = Show("SELECT * FROM emp WHERE mgr = NULL");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await diagnostics.AnalyzeNowAsync();

        var warning = Assert.Single(diagnostics.Current);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("LINT.EQUALS_NULL", warning.Code);
        Assert.Contains("IS NULL", warning.Message, StringComparison.Ordinal);
        window.Close();
        return 0;
    }));

    [Fact]
    public Task Lint_off_gives_no_warning() => Ui.Run(() => WithLint(false, async () =>
    {
        var (window, view, _) = Show("SELECT * FROM emp WHERE mgr = NULL");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await diagnostics.AnalyzeNowAsync();

        Assert.Empty(diagnostics.Current);
        window.Close();
        return 0;
    }));
}
