using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Project_Phantom;

public sealed partial class AudioVolumeTestDialog : ContentDialog
{
    private readonly ObservableCollection<ApplicationEntry> _sessions = new();
    private AudioSessionVolumeControl? _selectedControl;
    private bool _updatingSlider;

    public AudioVolumeTestDialog()
    {
        InitializeComponent();
        SessionsComboBox.ItemsSource = _sessions;
        Closed += AudioVolumeTestDialog_Closed;
    }

    public void LoadSessions()
    {
        ReleaseSelectedControl();
        SessionsComboBox.SelectedItem = null;
        _sessions.Clear();
        VolumeSlider.IsEnabled = false;
        SetSliderValue(0);
        VolumePercentTextBlock.Text = "—%";

        try
        {
            foreach (var session in AudioSessionService.GetActiveSessions())
            {
                _sessions.Add(session);
            }

            StatusTextBlock.Text = _sessions.Count == 0
                ? "No active audio sessions found. Start audio in an application, then refresh."
                : "Select a session to read and change only that application's session volume.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            StatusTextBlock.Text = "Windows audio sessions are temporarily unavailable. Try refreshing.";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSessions();
    }

    private void SessionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReleaseSelectedControl();
        VolumeSlider.IsEnabled = false;
        SetSliderValue(0);
        VolumePercentTextBlock.Text = "—%";

        if (SessionsComboBox.SelectedItem is not ApplicationEntry entry)
        {
            StatusTextBlock.Text = "Select a session to read and change only that application's session volume.";
            return;
        }

        try
        {
            _selectedControl = AudioSessionService.TryOpenActiveSession(entry);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _selectedControl = null;
        }

        if (_selectedControl is null || !_selectedControl.TryGetVolume(out var volume))
        {
            ReleaseSelectedControl();
            StatusTextBlock.Text = "That session is no longer available or cannot be controlled. Refresh and choose another.";
            return;
        }

        SetSliderValue(volume * 100f);
        VolumePercentTextBlock.Text = $"{Math.Round(volume * 100f)}%";
        VolumeSlider.IsEnabled = true;
        StatusTextBlock.Text = "Volume changes apply to this selected Windows audio session only.";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _selectedControl is null)
        {
            return;
        }

        var percent = (int)Math.Round(e.NewValue);
        if (!_selectedControl.TrySetVolume(percent / 100f))
        {
            ReleaseSelectedControl();
            VolumeSlider.IsEnabled = false;
            StatusTextBlock.Text = "The selected session closed or became unavailable. Refresh to choose another.";
            return;
        }

        VolumePercentTextBlock.Text = $"{percent}%";
    }

    private void SetSliderValue(double value)
    {
        _updatingSlider = true;
        VolumeSlider.Value = value;
        _updatingSlider = false;
    }

    private void ReleaseSelectedControl()
    {
        _selectedControl?.Dispose();
        _selectedControl = null;
    }

    private void AudioVolumeTestDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ReleaseSelectedControl();
    }
}
