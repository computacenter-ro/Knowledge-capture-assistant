using System.Collections.Specialized;
using System.ComponentModel;
using KnowledgeCapture.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace KnowledgeCapture.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel { get; } = AppHost.Chat;

    public ChatPage()
    {
        InitializeComponent();
        // keep the newest message in view while tokens stream in
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        // a voice answer landed in the box: focus it with the caret at the end, ready for a quick review
        ViewModel.TranscriptReady += () =>
        {
            InputBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            InputBox.Select(InputBox.Text.Length, 0);
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (MessageItem m in e.NewItems) m.PropertyChanged += OnMessagePropertyChanged;
        ScrollToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageItem.Text) && ReferenceEquals(sender, ViewModel.Messages.LastOrDefault())) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (ViewModel.Messages.Count > 0) MessageList.ScrollIntoView(ViewModel.Messages[^1], ScrollIntoViewAlignment.Leading);
    }

    private void Conversation_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenConversationCommand.Execute(e.ClickedItem as ConversationItem);

    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        e.Handled = true;
        if (ViewModel.RunCommand.CanExecute(null)) ViewModel.RunCommand.Execute(null);
    }
}
