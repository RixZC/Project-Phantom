using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Disturn;

public sealed partial class AddApplicationDialog : ContentDialog
{
    private readonly ObservableCollection<ApplicationEntry> _allProcesses = new();
    private readonly ObservableCollection<ApplicationEntry> _visibleEntries = new();
    private bool _processDiscoveryFailed;

    public IReadOnlyCollection<ApplicationEntry> ExistingApplications { get; set; } = Array.Empty<ApplicationEntry>();

    public ApplicationEntry? SelectedApplication => SessionsListView.SelectedItem as ApplicationEntry;

    public AddApplicationDialog()
    {
        InitializeComponent();
        SessionsListView.ItemsSource = _visibleEntries;
    }

    public void LoadProcesses()
    {
        _allProcesses.Clear();
        _processDiscoveryFailed = false;

        try
        {
            foreach (var process in AudioSessionService.GetRunningProcesses())
            {
                _allProcesses.Add(process);
            }
        }
        catch
        {
            _processDiscoveryFailed = true;
        }

        UpdateVisibleEntries();
    }

    private void RefreshProcessesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateVisibleEntries();
    }

    private void UpdateVisibleEntries()
    {
        SessionsListView.SelectedItem = null;
        IsPrimaryButtonEnabled = false;
        _visibleEntries.Clear();

        var availableProcesses = _allProcesses.Where(entry => !IsAlreadyInGroup(entry)).ToArray();
        var searchText = SearchTextBox.Text.Trim();
        foreach (var entry in availableProcesses.Where(entry =>
            string.IsNullOrEmpty(searchText) ||
            entry.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
        {
            _visibleEntries.Add(entry);
        }

        StatusTextBlock.Text = _processDiscoveryFailed
            ? "The running application list is temporarily unavailable. Try refreshing."
            : _allProcesses.Count == 0
                ? "No running applications found."
                : availableProcesses.Length == 0
                    ? "All running applications are already assigned to a group."
                    : _visibleEntries.Count == 0
                        ? "No matching applications."
                        : "A selected application may not currently have a Windows audio session that Disturn can control.";
        StatusTextBlock.Visibility = Visibility.Visible;
    }

    private bool IsAlreadyInGroup(ApplicationEntry entry)
    {
        return ExistingApplications.Any(existing => existing.IsSameApplication(entry));
    }

    private void SessionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        IsPrimaryButtonEnabled = SelectedApplication is not null;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedApplication is not { HasAudioSession: false } application)
        {
            return;
        }

        // Recheck by application identity so another process instance with the same
        // executable can keep this deduplicated choice valid if its representative exits.
        var stillRunning = false;
        try
        {
            stillRunning = AudioSessionService.GetRunningProcesses()
                .Any(process => process.IsSameApplication(application));
        }
        catch
        {
            // Treat a failed recheck as an unavailable process list below.
        }

        if (stillRunning)
        {
            return;
        }

        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        StatusTextBlock.Text = "That application has exited or is unavailable. Refresh the list and choose another.";
        StatusTextBlock.Visibility = Visibility.Visible;
        SessionsListView.SelectedItem = null;
    }
}
