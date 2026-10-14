using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
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
        private AppWindow? _appWindow;
        private TrayIconService? _trayIconService;
        private GroupItem? _selectedGroup;
        private bool _allowClose;

        public MainWindow()
        {
            InitializeComponent();
            Closed += MainWindow_Closed;

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is not null)
            {
                _appWindow.Closing += AppWindow_Closing;
                _trayIconService = new TrayIconService(windowHandle, DispatcherQueue, RestoreFromTray, ExitFromTray);
            }

            var loadResult = GroupPersistenceService.Load();
            _groupSavingAllowed = loadResult.CanSave;
            foreach (var savedGroup in loadResult.Groups)
            {
                var group = CreateGroup(savedGroup.Name, savedGroup.VolumePercent);
                group.TrayBackgroundColor = savedGroup.TrayBackgroundColor;
                group.TrayTextColor = savedGroup.TrayTextColor;
                group.UseAutomaticTrayTextColor = savedGroup.UseAutomaticTrayTextColor;
                group.AccentColor = savedGroup.AccentColor;
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
            if (_groups.Count > 0)
            {
                SelectGroup(_groups[0]);
            }
            else
            {
                _trayIconService?.UpdateSelectedGroup(null, 100);
            }

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
                var group = CreateGroup(dialog.GroupName);
                _groups.Add(group);
                if (_selectedGroup is null)
                {
                    SelectGroup(group);
                }

                EmptyGroupsTextBlock.Visibility = Visibility.Collapsed;
                SaveGroups();
            }
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                var wasSelected = ReferenceEquals(_selectedGroup, group);
                _groups.Remove(group);
                group.PropertyChanged -= Group_PropertyChanged;
                if (wasSelected)
                {
                    SelectGroup(_groups.FirstOrDefault());
                }

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
                TrayBackgroundColor = group.TrayBackgroundColor,
                TrayTextColor = group.TrayTextColor,
                UseAutomaticTrayTextColor = group.UseAutomaticTrayTextColor,
                AccentColor = group.AccentColor,
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
            if (sender is not GroupItem group)
            {
                return;
            }

            if (e.PropertyName == nameof(GroupItem.VolumePercent))
            {
                _volumeController?.ApplyGroup(group);
                if (ReferenceEquals(_selectedGroup, group))
                {
                    _trayIconService?.UpdateSelectedGroup(group.Name, group.VolumePercent, group.AccentColor);
                }
            }
            else if (e.PropertyName == nameof(GroupItem.AccentColor))
            {
                if (ReferenceEquals(_selectedGroup, group))
                {
                    _trayIconService?.UpdateSelectedGroup(group.Name, group.VolumePercent, group.AccentColor);
                }
            }
            else if (e.PropertyName != nameof(GroupItem.TrayBackgroundColor) &&
                e.PropertyName != nameof(GroupItem.TrayTextColor) &&
                e.PropertyName != nameof(GroupItem.UseAutomaticTrayTextColor))
            {
                return;
            }

            if (_groupSavingAllowed && _saveDebounceTimer is not null)
            {
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _trayIconService?.Dispose();
            _trayIconService = null;
            _saveDebounceTimer?.Stop();
            SaveGroups();
            _volumeController?.Dispose();
            _volumeController = null;
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            sender.Hide();
        }

        private void SelectGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                SelectGroup(group);
            }
        }

        private void SelectGroup(GroupItem? group)
        {
            if (group is not null && !_groups.Contains(group))
            {
                return;
            }

            if (ReferenceEquals(_selectedGroup, group))
            {
                return;
            }

            if (_selectedGroup is not null)
            {
                _selectedGroup.IsSelected = false;
            }

            _selectedGroup = group;
            if (_selectedGroup is not null)
            {
                _selectedGroup.IsSelected = true;
            }

            _trayIconService?.UpdateSelectedGroup(_selectedGroup?.Name, _selectedGroup?.VolumePercent ?? 100,
                _selectedGroup?.AccentColor);
        }

        private void AccentColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not GroupItem group) return;

            var rgb = Convert.ToUInt32(group.AccentColor[1..], 16);
            var picker = new ColorPicker
            {
                Width = 280,
                IsAlphaEnabled = false,
                Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            };
            // Initialize before subscribing so opening the popup does not change the group.
            picker.ColorChanged += (_, args) =>
                group.AccentColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            var flyout = new Flyout { Content = picker };
            flyout.ShowAt(button);
        }

        private void RestoreFromTray()
        {
            _appWindow?.Show();
            Activate();
        }

        private void ExitFromTray()
        {
            _allowClose = true;
            _trayIconService?.Dispose();
            _trayIconService = null;
            Close();
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
        private bool _isSelected;
        private string _trayBackgroundColor = GroupTrayColors.DefaultBackground;
        private string _trayTextColor = GroupTrayColors.DefaultText;
        private bool _useAutomaticTrayTextColor = true;
        private string _accentColor = GroupTrayColors.DefaultAccent;

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

        public string AccentColor
        {
            get => _accentColor;
            set
            {
                var color = GroupTrayColors.Normalize(value, GroupTrayColors.DefaultAccent);
                if (_accentColor == color) return;
                _accentColor = color;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentColor)));
            }
        }

        public string TrayBackgroundColor
        {
            get => _trayBackgroundColor;
            set
            {
                var color = GroupTrayColors.Normalize(value, GroupTrayColors.DefaultBackground);
                if (_trayBackgroundColor == color) return;
                _trayBackgroundColor = color;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayBackgroundColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTrayTextColor)));
            }
        }

        // Retain the manual choice even while automatic contrast is enabled.
        public string TrayTextColor
        {
            get => _trayTextColor;
            set
            {
                var color = GroupTrayColors.Normalize(value, GroupTrayColors.DefaultText);
                if (_trayTextColor == color) return;
                _trayTextColor = color;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayTextColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTrayTextColor)));
            }
        }

        public bool UseAutomaticTrayTextColor
        {
            get => _useAutomaticTrayTextColor;
            set
            {
                if (_useAutomaticTrayTextColor == value) return;
                _useAutomaticTrayTextColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseAutomaticTrayTextColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTrayTextColor)));
            }
        }

        public string EffectiveTrayTextColor => UseAutomaticTrayTextColor
            ? GroupTrayColors.AutomaticText(TrayBackgroundColor) : TrayTextColor;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionLabel)));
            }
        }

        public string SelectionLabel => IsSelected ? "Selected" : "Select";

        public GroupItem(string name, int volumePercent = 100)
        {
            Name = name;
            _volumePercent = Math.Clamp(volumePercent, 0, 100);
        }
    }
}
