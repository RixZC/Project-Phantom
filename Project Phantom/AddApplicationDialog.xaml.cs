using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace Project_Phantom;

public sealed partial class AddApplicationDialog : ContentDialog
{
    private readonly ObservableCollection<ApplicationEntry> _allSessions = new();
    private readonly ObservableCollection<ApplicationEntry> _allProcesses = new();
    private readonly ObservableCollection<ApplicationEntry> _visibleEntries = new();
    private bool _showingProcesses;

    public IReadOnlyCollection<ApplicationEntry> ExistingApplications { get; set; } = Array.Empty<ApplicationEntry>();

    public ApplicationEntry? SelectedApplication => SessionsListView.SelectedItem as ApplicationEntry;

    public AddApplicationDialog()
    {
        InitializeComponent();
        SessionsListView.ItemsSource = _visibleEntries;
    }

    public void LoadSessions()
    {
        _allSessions.Clear();

        try
        {
            foreach (var session in AudioSessionService.GetActiveSessions())
            {
                _allSessions.Add(session);
            }
        }
        catch
        {
            StatusTextBlock.Text = "Windows audio sessions are temporarily unavailable.";
            StatusTextBlock.Visibility = Visibility.Visible;
            return;
        }

        UpdateVisibleEntries();
    }

    private void BrowseProcessesButton_Click(object sender, RoutedEventArgs e)
    {
        _showingProcesses = true;
        ViewTitleTextBlock.Text = "All Applications";
        BrowseProcessesButton.Visibility = Visibility.Collapsed;
        BackToQuickAddButton.Visibility = Visibility.Visible;
        RefreshProcessesButton.Visibility = Visibility.Visible;
        LoadProcesses();
    }

    private void BackToQuickAddButton_Click(object sender, RoutedEventArgs e)
    {
        _showingProcesses = false;
        ViewTitleTextBlock.Text = "Quick Add";
        BrowseProcessesButton.Visibility = Visibility.Visible;
        BackToQuickAddButton.Visibility = Visibility.Collapsed;
        RefreshProcessesButton.Visibility = Visibility.Collapsed;
        UpdateVisibleEntries();
    }

    private void RefreshProcessesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
    }

    private void LoadProcesses()
    {
        _allProcesses.Clear();
        try
        {
            foreach (var process in AudioSessionService.GetRunningProcesses())
            {
                _allProcesses.Add(process);
            }
        }
        catch
        {
            StatusTextBlock.Text = "The running process list is temporarily unavailable.";
            StatusTextBlock.Visibility = Visibility.Visible;
            return;
        }

        UpdateVisibleEntries();
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

        var source = _showingProcesses ? _allProcesses : _allSessions;
        var availableEntries = source
            .Where(entry => !IsAlreadyInGroup(entry))
            .ToArray();
        var searchText = SearchTextBox.Text.Trim();
        foreach (var entry in availableEntries.Where(entry =>
            string.IsNullOrEmpty(searchText) ||
            entry.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.DeviceName.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
        {
            _visibleEntries.Add(entry);
        }

        if (_showingProcesses)
        {
            var resultMessage = _allProcesses.Count == 0
                ? "No running processes found."
                : availableEntries.Length == 0
                    ? "All running processes are already in this group."
                : _visibleEntries.Count == 0
                    ? "No matching processes."
                    : "A selected process may not currently have a Windows audio session that Phantom can control.";
            StatusTextBlock.Text = resultMessage;
            StatusTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            StatusTextBlock.Text = _allSessions.Count == 0
                ? "No active audio sessions found."
                : availableEntries.Length == 0
                    ? "All active audio sessions are already in this group."
                : "No matching applications found.";
            StatusTextBlock.Visibility = _visibleEntries.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private bool IsAlreadyInGroup(ApplicationEntry entry)
    {
        return ExistingApplications.Any(existing => existing.IsSameApplication(entry));
    }

    private void SessionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        IsPrimaryButtonEnabled = SessionsListView.SelectedItem is ApplicationEntry;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedApplication is not { HasAudioSession: false } application)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(application.ProcessId);
            if (!process.HasExited)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Treat an inaccessible process the same as one that exited while browsing.
        }

        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        StatusTextBlock.Text = "That process has exited or is unavailable. Refresh the list and choose another.";
        StatusTextBlock.Visibility = Visibility.Visible;
        SessionsListView.SelectedItem = null;
    }
}
