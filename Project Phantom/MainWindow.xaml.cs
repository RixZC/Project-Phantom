using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

// Hello
// Made with ❤️ by RixZC for Hundred Plus
// 9/30/2026
// This is the main window of the application.
// It is the first window that is shown when the application is launched.

// Project Phantom

namespace Project_Phantom
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<GroupItem> _groups = new();

        public MainWindow()
        {
            InitializeComponent();
            GroupsItemsControl.ItemsSource = _groups;
        }

        private async void AddGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddGroupDialog();
            dialog.ExistingGroupNames = _groups.Select(group => group.Name).ToArray();

            dialog.XamlRoot = Content.XamlRoot;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _groups.Add(new GroupItem(dialog.GroupName));
                EmptyGroupsTextBlock.Visibility = Visibility.Collapsed;
            }
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                _groups.Remove(group);
                EmptyGroupsTextBlock.Visibility = _groups.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    public sealed class GroupItem
    {
        public string Name { get; }
        public ObservableCollection<string> Applications { get; } = new();

        public GroupItem(string name)
        {
            Name = name;
        }
    }
}
