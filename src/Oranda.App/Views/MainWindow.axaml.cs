using Avalonia.Controls;
using Oranda.App;

namespace Oranda.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PerfMarks.Mark("window-created");
        Opened += OnOpenedForPerf;
    }

    private void OnOpenedForPerf(object? sender, EventArgs e)
    {
        Opened -= OnOpenedForPerf;
        RequestAnimationFrame(_ => PerfMarks.Mark("first-render"));
    }
}