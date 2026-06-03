using System.Windows.Controls;
using System.Windows.Input;
using DataVortex.App.ViewModels;

namespace DataVortex.App.Views;

public partial class FilesView : UserControl
{
    public FilesView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FilesViewModel vm && vm.SelectedFile is not null
            && vm.OpenFileCommand.CanExecute(vm.SelectedFile))
        {
            vm.OpenFileCommand.Execute(vm.SelectedFile);
        }
    }
}
