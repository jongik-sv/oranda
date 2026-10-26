using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Oranda.App.Shell;

namespace Oranda.App.Features.Update;

/// <summary>도움말 &gt; 새 기능(릴리스 노트) 창. 비모달이며 하나만 뜬다(다시 부르면 앞으로 가져온다).</summary>
public partial class ReleaseNotesWindow : Window
{
    private static ReleaseNotesWindow? _open;

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ReleaseNotesViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
                RenderSections(vm);
            }
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this))
            {
                _open = null;
            }
        };
    }

    /// <summary>열려 있는 창(없으면 null). 시험이 살펴본다.</summary>
    internal static ReleaseNotesWindow? Open => _open;

    /// <summary>버전마다 노트 한 덩어리를 그린다. 덩어리 사이에는 구분선을 둔다.</summary>
    private void RenderSections(ReleaseNotesViewModel vm)
    {
        if (this.FindControl<StackPanel>("SectionsPanel") is not { } panel)
        {
            return;
        }

        panel.Children.Clear();
        foreach (var section in vm.Sections)
        {
            if (panel.Children.Count > 0)
            {
                var line = new Border { Height = 1 };
                line.Bind(Border.BackgroundProperty, this.GetResourceObservable("OrandaSeparatorBrush"));
                panel.Children.Add(line);
            }

            var box = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Name = "Notes_" + section.Version };
            box.Inlines ??= new InlineCollection();
            var first = true;
            foreach (var noteLine in section.Lines)
            {
                if (!first)
                {
                    box.Inlines.Add(new LineBreak());
                }

                first = false;
                box.Inlines.Add(new Run(noteLine.Text) { FontWeight = noteLine.Bold ? FontWeight.Bold : FontWeight.Normal });
            }

            panel.Children.Add(box);
        }
    }

    /// <summary>창을 만들어 보인다. 이미 열려 있으면 앞으로 가져온다. 포함된 노트는 이때 처음 읽는다.</summary>
    public static ReleaseNotesWindow ShowFor(UpdateService service, ShellController shell)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return existing;
        }

        var installed = AppVersion.Installed;
        var vm = new ReleaseNotesViewModel(
            shell.Text,
            ReleaseNotesCatalog.Load(),
            installed ?? AppVersion.Assembly,
            installed is not null,
            ReleaseNotesCatalog.ReleasesUrl(service.ResolveFeed().Feed));
        var window = new ReleaseNotesWindow { DataContext = vm };
        _open = window;
        if (shell.MainWindow is { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        return window;
    }
}
