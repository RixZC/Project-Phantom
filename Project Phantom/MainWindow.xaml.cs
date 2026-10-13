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
using System.ComponentModel;
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
        private GroupVolumeController? _volumeController;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveDebounceTimer;

        public MainWindow()
        {
            InitializeComponent();
            Closed += MainWindow_Closed;

            var loadResult = GroupPersistenceService.Load();
            _groupSavingAllowed = loadResult.CanSave;
            foreach (var savedGroup in loadResult.Groups)
            {
                var group = CreateGroup(savedGroup.Name, savedGroup.VolumePercent);
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
            _volumeController = new GroupVolumeController(_groups, DispatcherQueue);
            _volumeController.Start();
            _saveDebounceTimer = DispatcherQueue.CreateTimer();
            _saveDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
            _saveDebounceTimer.IsRepeating = false;
            _saveDebounceTimer.Tick += (_, _) => SaveGroups();
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
                _groups.Add(CreateGroup(dialog.GroupName));
                EmptyGroupsTextBlock.Visibility = Visibility.Collapsed;
                SaveGroups();
            }
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                _groups.Remove(group);
                group.PropertyChanged -= Group_PropertyChanged;
                _volumeController?.Reconcile();
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
                    _volumeController?.Reconcile();
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
                ExistingApplications = _groups.SelectMany(item => item.Applications).ToArray()
            };

            dialog.Opened += (sender, args) => dialog.LoadProcesses();

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (dialog.SelectedApplication is ApplicationEntry application &&
                    !_groups.SelectMany(item => item.Applications).Any(existing => existing.IsSameApplication(application)))
                {
                    group.Applications.Add(application);
                    _volumeController?.Reconcile();
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
                VolumePercent = group.VolumePercent,
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

        private GroupItem CreateGroup(string name, int volumePercent = 100)
        {
            var group = new GroupItem(name, volumePercent);
            group.PropertyChanged += Group_PropertyChanged;
            return group;
        }

        private void Group_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not GroupItem group || e.PropertyName != nameof(GroupItem.VolumePercent))
            {
                return;
            }

            _volumeController?.ApplyGroup(group);
            if (_groupSavingAllowed && _saveDebounceTimer is not null)
            {
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _saveDebounceTimer?.Stop();
            SaveGroups();
            _volumeController?.Dispose();
            _volumeController = null;
        }

        private void ShowPersistenceWarning(string? warning)
        {
            PersistenceStatusTextBlock.Text = warning ?? "";
            PersistenceStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(warning)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    public sealed class GroupItem : INotifyPropertyChanged
    {
        public string Name { get; }
        public ObservableCollection<ApplicationEntry> Applications { get; } = new();
        private int _volumePercent = 100;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int VolumePercent
        {
            get => _volumePercent;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_volumePercent == clamped) return;
                _volumePercent = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumePercent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeLabel)));
            }
        }

        public string VolumeLabel => $"{VolumePercent}%";

        public GroupItem(string name, int volumePercent = 100)
        {
            Name = name;
            _volumePercent = Math.Clamp(volumePercent, 0, 100);
        }
    }
}
