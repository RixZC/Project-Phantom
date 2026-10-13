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
        private bool _groupSavingAllowed = true;

        public MainWindow()
        {
            InitializeComponent();

            var loadResult = GroupPersistenceService.Load();
            _groupSavingAllowed = loadResult.CanSave;
            foreach (var savedGroup in loadResult.Groups)
            {
                var group = new GroupItem(savedGroup.Name);
                foreach (var savedApplication in savedGroup.Applications)
                {
                    group.Applications.Add(new ApplicationEntry(
                        savedApplication.DisplayName,
                        savedApplication.ProcessName,
                        processId: 0,
                        executablePath: savedApplication.ExecutablePath));
                }

                _groups.Add(group);
            }

            GroupsItemsControl.ItemsSource = _groups;
            EmptyGroupsTextBlock.Visibility = _groups.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ShowPersistenceWarning(loadResult.Warning);
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
                SaveGroups();
            }
        }

        private async void AudioVolumeTestButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AudioVolumeTestDialog
            {
                XamlRoot = Content.XamlRoot
            };

            dialog.LoadSessions();
            await dialog.ShowAsync();
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                _groups.Remove(group);
                EmptyGroupsTextBlock.Visibility = _groups.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SaveGroups();
            }
        }

        private void RemoveApplicationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ApplicationEntry application)
            {
                var group = _groups.FirstOrDefault(candidate => candidate.Applications.Contains(application));
                if (group?.Applications.Remove(application) == true)
                {
                    SaveGroups();
                }
            }
        }

        private async void AddApplicationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not GroupItem group)
            {
                return;
            }

            var dialog = new AddApplicationDialog
            {
                XamlRoot = Content.XamlRoot,
                ExistingApplications = group.Applications.ToArray()
            };

            dialog.Opened += (sender, args) => dialog.LoadSessions();

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (dialog.SelectedApplication is ApplicationEntry application &&
                    !group.Applications.Any(existing => existing.IsSameApplication(application)))
                {
                    group.Applications.Add(application);
                    SaveGroups();
                }
            }
        }

        private void SaveGroups()
        {
            if (!_groupSavingAllowed)
            {
                return;
            }

            var savedGroups = _groups.Select(group => new SavedGroupData
            {
                Name = group.Name,
                Applications = group.Applications.Select(application => new SavedApplicationData
                {
                    DisplayName = application.DisplayName,
                    ProcessName = application.ProcessName,
                    ExecutablePath = application.ExecutablePath
                }).ToList()
            }).ToList();

            var saveResult = GroupPersistenceService.Save(savedGroups);
            ShowPersistenceWarning(saveResult.Warning);
        }

        private void ShowPersistenceWarning(string? warning)
        {
            PersistenceStatusTextBlock.Text = warning ?? "";
            PersistenceStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(warning)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    public sealed class GroupItem
    {
        public string Name { get; }
        public ObservableCollection<ApplicationEntry> Applications { get; } = new();

        public GroupItem(string name)
        {
            Name = name;
        }
    }
}
