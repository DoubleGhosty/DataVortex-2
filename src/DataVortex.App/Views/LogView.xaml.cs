using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using DataVortex.App.ViewModels;

namespace DataVortex.App.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm && vm.Entries is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
        }
    }
}
