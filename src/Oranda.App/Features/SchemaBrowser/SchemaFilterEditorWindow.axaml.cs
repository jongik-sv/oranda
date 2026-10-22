using Avalonia.Controls;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>저장 필터 편집 대화상자(TSK-13-10). 확인은 검사(<see cref="SchemaFilterEditorViewModel.Validate"/>)를 통과해야 true 로 닫는다.</summary>
public partial class SchemaFilterEditorWindow : Window
{
    public SchemaFilterEditorWindow()
    {
        InitializeComponent();
        NewButton.Content = ShellIcons.Create("add");
        SaveButton.Content = ShellIcons.Create("save");
        DeleteButton.Content = ShellIcons.Create("delete");
        AddSchemaButton.Content = ShellIcons.Create("add");
        RemoveSchemaButton.Content = ShellIcons.Create("remove");

        NewButton.Click += (_, _) =>
        {
            Vm?.New();
            NameBox.Focus();
            NameBox.SelectAll();
        };
        SaveButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.SaveAsync();
            }
        };
        DeleteButton.Click += (_, _) => Vm?.Delete();
        AddSchemaButton.Click += (_, _) => Vm?.AddSchema();
        RemoveSchemaButton.Click += (_, _) => Vm?.RemoveSchema(SchemaList.SelectedItem as string);
        SelectAllButton.Click += (_, _) => Vm?.SelectAll();
        DeselectAllButton.Click += (_, _) => Vm?.DeselectAll();
        AssignButton.Click += (_, _) => Vm?.AssignGlobally();
        ResetButton.Click += (_, _) => Vm?.Reset();
        OkButton.Click += (_, _) =>
        {
            if (Vm?.Validate() == true)
            {
                Close(true);
            }
        };
        CancelButton.Click += (_, _) => Close(false);
    }

    private SchemaFilterEditorViewModel? Vm => DataContext as SchemaFilterEditorViewModel;
}
