using KnowledgeCapture.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KnowledgeCapture.Views;

public sealed partial class StoredDataPage : Page
{
    public StoredDataViewModel ViewModel { get; } = AppHost.StoredData;

    public StoredDataPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Refresh();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Select((sender as ListView)?.SelectedItem as StoredConversationItem);
}
