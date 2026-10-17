using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

// Hello
// Made with â¤ï¸ by RixZC for Hundred Plus
// 9/30/2026
// This is the main window of the application.
// It is the first window that is shown when the application is launched.

// Disturn

namespace Disturn
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<GroupItem> _groups = new();
        private bool _groupSavingAllowed = true;
        private GroupVolumeController? _volumeController;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveDebounceTimer;
        private AppWindow? _appWindow;
        private TrayIconService? _trayIconService;
        private TrayMenuWindow? _trayMenuWindow;
        private GlobalHotkeyService? _hotkeyService;
        private SavedApplicationSettings _settings = new();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _automaticReturnTimer;
        private long? _returnDeadline;
        private bool _updatingDefaultControls;
        private bool _updatingStartupControls;
        private bool _startupOperation;
        private bool _startupReady;
        private GroupItem? _selectedGroup;
        private bool _allowClose;
        private XamlRoot? _chromeXamlRoot;
        private Storyboard? _viewTransition;
#if DEBUG
        private int _windowDebugLines;
        private int _chromeDebugDepth;
#endif

        internal bool StartMinimizedToTray => _settings.StartMinimizedToTray && _trayIconService is not null;

        internal void OpenFromLaunch() => RestoreFromTray();

        internal async Task InitializeStartupAsync()
        {
            ApplyStartupStatus(await StartupService.ReadAsync());
            StartMinimizedToggle.IsOn = _settings.StartMinimizedToTray;
            LaunchWithWindowsToggle.Toggled += LaunchWithWindowsToggle_Toggled;
            StartMinimizedToggle.Toggled += StartMinimizedToggle_Toggled;
            _startupReady = true;
            // Reflect Task Manager/Settings changes when the window is opened again.
            Activated += async (_, args) =>
            {
                TraceWindowState($"Startup activation: {args.WindowActivationState}");
                if (!_startupReady || _startupOperation || _allowClose ||
                    args.WindowActivationState == WindowActivationState.Deactivated) return;
                _startupOperation = true;
                try
                {
                    ApplyStartupStatus(await StartupService.ReadAsync());
                    TraceWindowState("Startup activation refresh completed");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowDebug] Startup activation exception: {ex}");
                    throw;
                }
                finally { _startupOperation = false; }
            };
        }

        private void ApplyStartupStatus(StartupService.Status status)
        {
            var changed = status.Enabled is bool enabled && _settings.LaunchWithWindows != enabled;
            if (status.Enabled is bool actual) _settings.LaunchWithWindows = actual;
            _updatingStartupControls = true;
            try
            {
                LaunchWithWindowsToggle.IsOn = status.Enabled == true;
                LaunchWithWindowsToggle.IsEnabled = status.CanChange;
                StartMinimizedToggle.IsEnabled = status.Enabled == true;
                StartupStatusText.Text = status.Warning ?? "";
                StartupStatusText.Visibility = status.Warning is null ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { _updatingStartupControls = false; }
            if (changed) SaveGroups();
        }

        private async void LaunchWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingStartupControls || _startupOperation || _allowClose) return;
            _startupOperation = true;
            var requested = LaunchWithWindowsToggle.IsOn;
            LaunchWithWindowsToggle.IsEnabled = false;
            StartMinimizedToggle.IsEnabled = false;
            try { ApplyStartupStatus(await StartupService.SetEnabledAsync(requested)); }
            finally { _startupOperation = false; }
        }

        private void StartMinimizedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingStartupControls || _startupOperation || !StartMinimizedToggle.IsEnabled || _allowClose) return;
            _settings.StartMinimizedToTray = StartMinimizedToggle.IsOn;
            SaveGroups();
        }

        public MainWindow()
        {
            InitializeComponent();
            var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
            AppVersionText.Text = $"V{version ?? "Unknown"}";
            Closed += MainWindow_Closed;

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is not null)
            {
                _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Disturn.ico"));
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(TitleBarDragRegion);
                _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
                _appWindow.Changed += AppWindow_ChromeChanged;
                WindowLayout.Loaded += WindowLayout_Loaded;
                WindowLayout.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
                Activated += (_, args) =>
                {
                    TraceWindowState($"Chrome activation: {args.WindowActivationState}");
                    ChromeTitleText.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.6 : 1;
                };
                UpdateTitleBarMetrics();
                UpdateTitleBarColors();
                _appWindow.Closing += AppWindow_Closing;
                _trayIconService = new TrayIconService(windowHandle, DispatcherQueue, RestoreFromTray, ExitFromTray,
                    ShowTrayMenu, OpenSettingsFromTray);
            }

            var loadResult = GroupPersistenceService.Load();
            _settings = loadResult.Settings;
            _groupSavingAllowed = loadResult.CanSave;
            foreach (var savedGroup in loadResult.Groups)
            {
                var group = CreateGroup(savedGroup.Name, savedGroup.VolumePercent, savedGroup.Id);
                group.TrayBackgroundColor = savedGroup.TrayBackgroundColor;
                group.TrayTextColor = savedGroup.TrayTextColor;
                group.UseAutomaticTrayTextColor = savedGroup.UseAutomaticTrayTextColor;
                group.AccentColor = savedGroup.AccentColor;
                group.Shortcut = GroupPersistenceService.ReadShortcut(savedGroup.Shortcut);
                group.HasShortcutData = savedGroup.Shortcut.ValueKind != System.Text.Json.JsonValueKind.Undefined;
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
            _automaticReturnTimer = DispatcherQueue.CreateTimer();
            _automaticReturnTimer.IsRepeating = false;
            _automaticReturnTimer.Tick += AutomaticReturnTimer_Tick;
            _trayIconService?.UpdateSelectedGroup(null, 100);
            SelectGroup(GetDefaultTarget());

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
            _hotkeyService = new GlobalHotkeyService(windowHandle, SelectGroup, ShowHotkeyWarning, AdjustVolume);
            _hotkeyService.StatusChanged += UpdateVolumeShortcutLabels;
            var shortcutWarnings = new List<string>();
            foreach (var group in _groups)
            {
                if (!_hotkeyService.Restore(group, out var error))
                    shortcutWarnings.Add($"{group.Name}: {error}");
            }
            foreach (var command in new[] { VolumeCommand.Up, VolumeCommand.Down })
            {
                var saved = command == VolumeCommand.Up ? _settings.VolumeUpShortcut : _settings.VolumeDownShortcut;
                if (!_hotkeyService.RestoreVolumeCommand(command, GroupPersistenceService.ReadShortcut(saved), out var error))
                    shortcutWarnings.Add($"Volume {command}: {error}");
            }
            UpdateVolumeShortcutLabels();
            ShowHotkeyWarning(shortcutWarnings.Count == 0 ? null : string.Join(" ", shortcutWarnings));
            // Load before subscribing so initialization cannot save over existing settings.
            TrayAppearanceComboBox.SelectedItem = TrayAppearanceComboBox.Items.OfType<ComboBoxItem>()
                .First(item => (string)item.Tag == _settings.TrayIconAppearance);
            _trayIconService?.SetAppearance(_settings.TrayIconAppearance);
            TrayAppearanceComboBox.SelectionChanged += TrayAppearanceComboBox_SelectionChanged;
            VolumeStepSlider.Value = _settings.VolumeStepPercent;
            VolumeStepText.Text = $"{_settings.VolumeStepPercent}%";
            VolumeStepSlider.ValueChanged += VolumeStepSlider_ValueChanged;
            RefreshDefaultTargets();
            AutomaticReturnToggle.IsOn = _settings.AutomaticReturnEnabled;
            ReturnDelayComboBox.SelectedItem = ReturnDelayComboBox.Items.OfType<ComboBoxItem>()
                .First(item => (string)item.Tag == _settings.ReturnDelaySeconds.ToString());
            ReturnDelayComboBox.IsEnabled = _settings.AutomaticReturnEnabled;
            DefaultTargetComboBox.SelectionChanged += DefaultTargetComboBox_SelectionChanged;
            AutomaticReturnToggle.Toggled += AutomaticReturnToggle_Toggled;
            ReturnDelayComboBox.SelectionChanged += ReturnDelayComboBox_SelectionChanged;
            if (loadResult.NeedsSave) SaveGroups();
        }

        // Both views and their services remain alive throughout navigation.
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsView();

        private void ShowSettingsView()
        {
            if (SettingsView.Visibility != Visibility.Visible)
                SwitchView(SoundMixerView, SettingsView, forward: true);
            SettingsBackButton.Focus(FocusState.Programmatic);
        }

        private async void OpenTaskbarSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            button.IsEnabled = false;
            TaskbarSettingsStatusText.Text = "";
            TaskbarSettingsStatusText.Visibility = Visibility.Collapsed;
            var launched = false;
            try
            {
                launched = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));
            }
            catch (Exception ex)
            {
                // Keep a failed external Settings launch from escaping this UI callback.
                System.Diagnostics.Debug.WriteLine("Could not open Windows taskbar settings: " + ex);
            }
            finally
            {
                button.IsEnabled = true;
                if (!launched)
                {
                    TaskbarSettingsStatusText.Text = "Windows Settings could not be opened. Open Settings > Personalization > Taskbar manually.";
                    TaskbarSettingsStatusText.Visibility = Visibility.Visible;
                }
            }
        }

        private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchView(SettingsView, SoundMixerView, forward: false);
            OpenSettingsButton.Focus(FocusState.Programmatic);
        }

        private void SwitchView(FrameworkElement outgoing, FrameworkElement incoming, bool forward)
        {
            _viewTransition?.Stop();
            _viewTransition = null;
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;

            var translation = new TranslateTransform();
            incoming.RenderTransform = translation;
            var slide = new DoubleAnimation
            {
                From = forward ? 40 : -40,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(180))
            };
            Storyboard.SetTarget(slide, translation);
            Storyboard.SetTargetProperty(slide, "X");
            Storyboard.SetTarget(fade, incoming);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var transition = new Storyboard();
            transition.Children.Add(slide);
            transition.Children.Add(fade);
            transition.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_viewTransition, transition)) return;
                transition.Stop();
                _viewTransition = null;
            };
            _viewTransition = transition;
            transition.Begin();
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
                RefreshDefaultTargets();
                // Creating a group does not select it; keep Master Volume/logo until selected.

                EmptyGroupsTextBlock.Visibility = Visibility.Collapsed;
                // Initial defaults for newly created first through fourth groups; never
                // overwrite an existing group's customized or cleared shortcut.
                if (_groups.Count <= 4 && _hotkeyService is not null)
                {
                    _hotkeyService.TryAssign(group, new GroupShortcut((uint)(0x74 + _groups.Count - 1)), out var error);
                    ShowHotkeyWarning(error);
                }
                SaveGroups();
            }
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GroupItem group)
            {
                if (_hotkeyService is not null && !_hotkeyService.TryAssign(group, null, out var error))
                {
                    ShowHotkeyWarning(error);
                    return;
                }
                var wasSelected = ReferenceEquals(_selectedGroup, group);
                _hotkeyService?.ForgetGroup(group);
                _groups.Remove(group);
                group.PropertyChanged -= Group_PropertyChanged;
                if (_settings.DefaultGroupId == group.Id)
                    _settings.DefaultGroupId = null;
                RefreshDefaultTargets();
                if (wasSelected)
                {
                    SelectGroup(GetDefaultTarget());
                }
                else RestartAutomaticReturn();

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
                Id = group.Id,
                Name = group.Name,
                VolumePercent = group.VolumePercent,
                TrayBackgroundColor = group.TrayBackgroundColor,
                TrayTextColor = group.TrayTextColor,
                UseAutomaticTrayTextColor = group.UseAutomaticTrayTextColor,
                AccentColor = group.AccentColor,
                Shortcut = GroupPersistenceService.WriteShortcut(group.Shortcut, group.HasShortcutData),
                Applications = group.Applications.Select(application => new SavedApplicationData
                {
                    DisplayName = application.DisplayName,
                    ProcessName = application.ProcessName,
                    ExecutablePath = application.ExecutablePath
                }).ToList()
            }).ToList();

            if (_hotkeyService is not null)
            {
                _settings.VolumeUpShortcut = GroupPersistenceService.WriteShortcut(
                    _hotkeyService.GetVolumeShortcut(VolumeCommand.Up), true);
                _settings.VolumeDownShortcut = GroupPersistenceService.WriteShortcut(
                    _hotkeyService.GetVolumeShortcut(VolumeCommand.Down), true);
            }
            var saveResult = GroupPersistenceService.Save(savedGroups, _settings);
            ShowPersistenceWarning(saveResult.Warning);
        }

        private GroupItem CreateGroup(string name, int volumePercent = 100, Guid? id = null)
        {
            var group = new GroupItem(name, volumePercent, id);
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
                e.PropertyName != nameof(GroupItem.UseAutomaticTrayTextColor) &&
                e.PropertyName != nameof(GroupItem.Shortcut))
            {
                return;
            }

            if (_groupSavingAllowed && _saveDebounceTimer is not null)
            {
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void WindowLayout_Loaded(object sender, RoutedEventArgs e)
        {
            WindowLayout.Loaded -= WindowLayout_Loaded;
            _chromeXamlRoot = WindowLayout.XamlRoot;
            if (_chromeXamlRoot is not null) _chromeXamlRoot.Changed += TitleBarXamlRoot_Changed;
            UpdateTitleBarMetrics();
            UpdateTitleBarColors();
        }

        private void AppWindow_ChromeChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            TraceWindowState($"AppWindow.Changed: size={args.DidSizeChange}, position={args.DidPositionChange}, presenter={args.DidPresenterChange}, visibility={args.DidVisibilityChange}");
            UpdateTitleBarMetrics();
        }

        private void TitleBarXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            TraceWindowState("XamlRoot.Changed");
            UpdateTitleBarMetrics();
        }

        private void UpdateTitleBarMetrics()
        {
            if (_appWindow is null) return;
            // Minimized caption metrics can be transiently negative (for example,
            // RightInset = -8). Keep the last valid XAML layout until restoration.
            if (_appWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                TraceWindowState("Title-bar metrics skipped: minimized");
                return;
            }
#if DEBUG
            _chromeDebugDepth++;
#endif
            TraceWindowState("Title-bar metrics BEGIN");
            try
            {
                var scale = WindowLayout.XamlRoot?.RasterizationScale ?? 1;
                var titleBar = _appWindow.TitleBar;
                var leftInset = titleBar.LeftInset;
                var rightInset = titleBar.RightInset;
                var nativeHeight = titleBar.Height;
                if (!double.IsFinite(scale) || scale <= 0 ||
                    leftInset < 0 || rightInset < 0 || nativeHeight <= 0)
                {
                    TraceWindowState("Title-bar metrics skipped: invalid native metrics/scale");
                    return;
                }
                var leftWidth = leftInset / scale;
                var rightWidth = rightInset / scale;
                var height = Math.Max(48, nativeHeight / scale);
                // Validate the entire snapshot before assigning any layout property.
                if (!double.IsFinite(leftWidth) || leftWidth < 0 ||
                    !double.IsFinite(rightWidth) || rightWidth < 0 ||
                    !double.IsFinite(height) || height <= 0)
                {
                    TraceWindowState("Title-bar metrics skipped: invalid logical dimensions");
                    return;
                }
                // AppWindow metrics are physical pixels; XAML uses logical pixels.
                // Only the middle column is draggable; caption-button insets stay outside it.
                TitleBarLeftInset.Width = new GridLength(leftWidth);
                TitleBarRightInset.Width = new GridLength(rightWidth);
                ChromeTitleBar.Height = height;
                TraceWindowState("Title-bar metrics END");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowDebug] Title-bar metrics exception; HRESULT=0x{ex.HResult:X8}: {ex}");
                throw; // Diagnostics only: preserve the original exception behavior.
            }
            finally
            {
#if DEBUG
                _chromeDebugDepth--;
#endif
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void TraceWindowState(string reason)
        {
#if DEBUG
            // Temporary, bounded trace. Never let diagnostic getters cause a new crash.
            if (++_windowDebugLines > 300) return;
            try
            {
                var root = WindowLayout.XamlRoot;
                var bar = _appWindow?.TitleBar;
                var state = (_appWindow?.Presenter as OverlappedPresenter)?.State;
                System.Diagnostics.Debug.WriteLine(
                    $"[WindowDebug] #{_windowDebugLines} {reason}; thread={Environment.CurrentManagedThreadId}; UI={DispatcherQueue.HasThreadAccess}; " +
                    $"visible={_appWindow?.IsVisible}; presenter={state}; windowSize={_appWindow?.Size}; " +
                    $"rootSize={root?.Size}; scale={root?.RasterizationScale}; rootVisible={root?.IsHostVisible}; " +
                    $"insets={bar?.LeftInset}/{bar?.RightInset}; nativeHeight={bar?.Height}; " +
                    $"xamlHeight={ChromeTitleBar.Height}; actualHeight={ChromeTitleBar.ActualHeight}; metricsDepth={_chromeDebugDepth}; exiting={_allowClose}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WindowDebug] State trace failed: {ex}"); }
#endif
        }

        private void UpdateTitleBarColors()
        {
            if (_appWindow is null) return;
            var titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = WindowLayout.ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _trayMenuWindow?.Close();
            _trayMenuWindow = null;
            _viewTransition?.Stop();
            _viewTransition = null;
            TraceWindowState("MainWindow.Closed");
            if (_appWindow is not null) _appWindow.Changed -= AppWindow_ChromeChanged;
            if (_chromeXamlRoot is not null) _chromeXamlRoot.Changed -= TitleBarXamlRoot_Changed;
            StopAutomaticReturn();
            _hotkeyService?.Dispose();
            _hotkeyService = null;
            _trayIconService?.Dispose();
            _trayIconService = null;
            _saveDebounceTimer?.Stop();
            SaveGroups();
            _volumeController?.Dispose();
            _volumeController = null;
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            TraceWindowState("AppWindow.Closing");
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
                SelectGroup(group);
        }

        private async void ShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not GroupItem group) return;
            var dialog = new GroupShortcutDialog(group.Shortcut, (shortcut, allowReassign) =>
            {
                if (!_groups.Contains(group)) return "This group no longer exists.";
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                if (!_hotkeyService.TryAssign(group, shortcut, out var error, allowReassign)) return error;
                ShowHotkeyWarning(null);
                _saveDebounceTimer?.Stop();
                SaveGroups(); // Includes both groups after a confirmed transfer.
                return null;
            }, () =>
            {
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                _hotkeyService.TrySuspendForRecording(out var error);
                if (error is not null) ShowHotkeyWarning(error);
                return error;
            }, () =>
            {
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                _hotkeyService.TryResumeAfterRecording(out var error);
                ShowHotkeyWarning(error);
                return error;
            }, shortcut => _hotkeyService?.FindConflictName(group, null, shortcut), group.ShortcutError, observeOnly: true)
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
                ReportRawInputWarning = ShowHotkeyWarning,
                XamlRoot = Content.XamlRoot,
                Title = $"Shortcut for {group.Name}"
            };
            await dialog.ShowAsync();
        }

        private void UpdateVolumeShortcutLabels()
        {
            VolumeUpShortcutText.Text = _hotkeyService?.GetVolumeLabel(VolumeCommand.Up) ?? "Not assigned";
            VolumeDownShortcutText.Text = _hotkeyService?.GetVolumeLabel(VolumeCommand.Down) ?? "Not assigned";
        }

        private async void VolumeShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || _hotkeyService is null) return;
            var command = button.Tag as string == "Up" ? VolumeCommand.Up : VolumeCommand.Down;
            var dialog = new GroupShortcutDialog(_hotkeyService.GetVolumeShortcut(command), (shortcut, allowReassign) =>
            {
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                if (!_hotkeyService.TryAssignVolumeCommand(command, shortcut, out var error, allowReassign)) return error;
                ShowHotkeyWarning(null);
                _saveDebounceTimer?.Stop();
                SaveGroups(); // Saves the command and any previous owner cleared by reassignment together.
                return null;
            }, () =>
            {
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                _hotkeyService.TrySuspendForRecording(out var error);
                if (error is not null) ShowHotkeyWarning(error);
                return error;
            }, () =>
            {
                if (_hotkeyService is null) return "Global shortcuts are unavailable.";
                _hotkeyService.TryResumeAfterRecording(out var error);
                ShowHotkeyWarning(error);
                return error;
            }, shortcut => _hotkeyService?.FindConflictName(null, command, shortcut), _hotkeyService.GetVolumeError(command))
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
                ReportRawInputWarning = ShowHotkeyWarning,
                XamlRoot = Content.XamlRoot,
                Title = $"Volume {command} shortcut"
            };
            await dialog.ShowAsync();
        }

        private void ShowHotkeyWarning(string? warning)
        {
            HotkeyStatusTextBlock.Text = warning ?? "";
            HotkeyStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(warning)
                ? Visibility.Collapsed : Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(warning))
                System.Diagnostics.Debug.WriteLine(warning);
        }

        private void SelectGroup(GroupItem? group)
        {
            if (group is not null && !_groups.Contains(group))
            {
                return;
            }

            // Explicit selection of the same non-default group is activity too.
            // Set the timer after updating selection, or before returning unchanged.
            if (ReferenceEquals(_selectedGroup, group))
            {
                RestartAutomaticReturn();
                return;
            }

            if (_selectedGroup is not null) _selectedGroup.IsSelected = false;
            _selectedGroup = group;
            if (_selectedGroup is not null) _selectedGroup.IsSelected = true;

            _trayIconService?.UpdateSelectedGroup(_selectedGroup?.Name, _selectedGroup?.VolumePercent ?? 100,
                _selectedGroup?.AccentColor);
            RestartAutomaticReturn();
        }

        private GroupItem? GetDefaultTarget() => _groups.FirstOrDefault(group => group.Id == _settings.DefaultGroupId);

        private void StopAutomaticReturn()
        {
            _returnDeadline = null;
            _automaticReturnTimer?.Stop();
        }

        private void RestartAutomaticReturn()
        {
            StopAutomaticReturn();
            if (_allowClose || !_settings.AutomaticReturnEnabled ||
                ReferenceEquals(_selectedGroup, GetDefaultTarget()) || _automaticReturnTimer is null) return;
            _returnDeadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                (long)_settings.ReturnDelaySeconds * System.Diagnostics.Stopwatch.Frequency;
            _automaticReturnTimer.Interval = TimeSpan.FromSeconds(_settings.ReturnDelaySeconds);
            _automaticReturnTimer.Start();
        }

        private void AutomaticReturnTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (_allowClose || !_settings.AutomaticReturnEnabled || _returnDeadline is not long deadline ||
                ReferenceEquals(_selectedGroup, GetDefaultTarget()))
            {
                StopAutomaticReturn();
                return;
            }
            // Stop/Start can leave a queued tick. Always check the current monotonic
            // deadline, so an old callback cannot override a newer selection/command.
            var remaining = deadline - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                sender.Interval = TimeSpan.FromSeconds(Math.Max(0.001,
                    (double)remaining / System.Diagnostics.Stopwatch.Frequency));
                sender.Start();
                return;
            }
            StopAutomaticReturn();
            SelectGroup(GetDefaultTarget());
        }

        private void RefreshDefaultTargets()
        {
            _updatingDefaultControls = true;
            try
            {
                DefaultTargetComboBox.Items.Clear();
                var master = new ComboBoxItem { Content = "Master Volume", Tag = Guid.Empty };
                DefaultTargetComboBox.Items.Add(master);
                DefaultTargetComboBox.SelectedItem = master;
                foreach (var group in _groups)
                {
                    var item = new ComboBoxItem { Content = group.Name, Tag = group.Id };
                    DefaultTargetComboBox.Items.Add(item);
                    if (group.Id == _settings.DefaultGroupId) DefaultTargetComboBox.SelectedItem = item;
                }
            }
            finally { _updatingDefaultControls = false; }
        }

        private void DefaultTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingDefaultControls || DefaultTargetComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not Guid id) return;
            _settings.DefaultGroupId = id == Guid.Empty ? null : id;
            SelectGroup(GetDefaultTarget());
            SaveGroups();
        }

        private void AutomaticReturnToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.AutomaticReturnEnabled = AutomaticReturnToggle.IsOn;
            ReturnDelayComboBox.IsEnabled = AutomaticReturnToggle.IsOn;
            RestartAutomaticReturn();
            SaveGroups();
        }

        private void ReturnDelayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReturnDelayComboBox.SelectedItem is not ComboBoxItem item ||
                !int.TryParse(item.Tag as string, out var seconds)) return;
            _settings.ReturnDelaySeconds = seconds;
            RestartAutomaticReturn();
            SaveGroups();
        }

        private void TrayAppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrayAppearanceComboBox.SelectedItem is not ComboBoxItem item) return;
            _settings.TrayIconAppearance = item.Tag as string ?? TrayIconAppearance.Automatic;
            _trayIconService?.SetAppearance(_settings.TrayIconAppearance);
            SaveGroups();
        }

        private void VolumeStepSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _settings.VolumeStepPercent = (int)Math.Round(e.NewValue);
            VolumeStepText.Text = $"{_settings.VolumeStepPercent}%";
            _saveDebounceTimer?.Stop();
            _saveDebounceTimer?.Start();
        }

        private void AdjustVolume(int direction)
        {
            if (!DispatcherQueue.HasThreadAccess)
                throw new InvalidOperationException("Volume commands must run on the UI thread.");
            if (_allowClose || direction == 0) return;
            // Only handled commands reset inactivity; model/mixer changes do not.
            RestartAutomaticReturn();
            var stepPercent = _settings.VolumeStepPercent;
            if (_selectedGroup is not null)
            {
                _selectedGroup.VolumePercent = Math.Clamp(
                    _selectedGroup.VolumePercent + Math.Sign(direction) * stepPercent, 0, 100);
            }
            else if (!MasterVolumeService.TryAdjust(direction, stepPercent, out var error))
            {
                ShowHotkeyWarning(error);
            }
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

        private void OpenSettingsFromTray()
        {
            if (_allowClose) return;
            RestoreFromTray();
            ShowSettingsView();
        }

        private void ShowTrayMenu(Windows.Graphics.RectInt32 iconBounds)
        {
            if (_allowClose) return;
            if (_trayMenuWindow is null)
            {
                var menu = new TrayMenuWindow(WindowLayout.ActualTheme, RestoreFromTray, OpenSettingsFromTray, ExitFromTray);
                menu.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_trayMenuWindow, menu)) _trayMenuWindow = null;
                };
                _trayMenuWindow = menu;
            }
            var currentMenu = _trayMenuWindow;
            try { currentMenu.ShowAt(iconBounds); }
            catch
            {
                currentMenu.Close();
                throw; // TrayIconService shows the original native menu on failure.
            }
        }

        private void RestoreFromTray()
        {
            TraceWindowState("RestoreFromTray ENTER (tray/manual launch path)");
            if (_allowClose) return; // Ignore an Open callback queued during Exit.
            // Visibility and presenter state are separate: Show alone can leave
            // an already-minimized window minimized. Activate only after restoring.
            _appWindow?.Show(activateWindow: false);
            if (_appWindow?.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore(activateWindow: false);
            }
            Activate();
            TraceWindowState("RestoreFromTray EXIT");
        }

        private void ExitFromTray()
        {
            _allowClose = true;
            _trayMenuWindow?.Close();
            _trayMenuWindow = null;
            StopAutomaticReturn();
            _hotkeyService?.Dispose();
            _hotkeyService = null;
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
        public Guid Id { get; }
        public string Name { get; }
        public ObservableCollection<ApplicationEntry> Applications { get; } = new();
        private int _volumePercent = 100;
        private bool _isSelected;
        private string _trayBackgroundColor = GroupTrayColors.DefaultBackground;
        private string _trayTextColor = GroupTrayColors.DefaultText;
        private bool _useAutomaticTrayTextColor = true;
        private string _accentColor = GroupTrayColors.DefaultAccent;
        private GroupShortcut? _shortcut;
        internal bool HasShortcutData { get; set; } = true;
        public bool IsShortcutActive { get; private set; }
        public string? ShortcutError { get; private set; }

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

        public GroupShortcut? Shortcut
        {
            get => _shortcut;
            internal set
            {
                var changed = _shortcut != value || !HasShortcutData;
                HasShortcutData = true;
                if (!changed) return;
                _shortcut = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shortcut)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutLabel)));
            }
        }

        public string ShortcutLabel => Shortcut is null ? "Not assigned"
            : Shortcut.Label + (IsShortcutActive ? "" : " (unavailable)");

        internal void SetShortcutStatus(bool active, string? error)
        {
            if (IsShortcutActive == active && ShortcutError == error) return;
            IsShortcutActive = active;
            ShortcutError = error;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShortcutActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutError)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutLabel)));
        }

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

        public GroupItem(string name, int volumePercent = 100, Guid? id = null)
        {
            Id = id ?? Guid.NewGuid();
            Name = name;
            _volumePercent = Math.Clamp(volumePercent, 0, 100);
        }
    }
}


