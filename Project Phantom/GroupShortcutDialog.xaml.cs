using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Project_Phantom;

public sealed partial class GroupShortcutDialog : ContentDialog
{
    private readonly Func<GroupShortcut?, bool, string?> _tryAssign;
    private readonly Func<GroupShortcut, string?> _findOwner;
    private readonly Func<string?> _suspend;
    private readonly Func<string?> _resume;
    private GroupShortcut? _candidate;
    private GroupShortcut? _beforeRecording;
    private bool _recording;
    private GroupShortcut? _pendingReassignment;
    private uint _capturedKeyToSuppress;
    private RecorderRawInputService? _rawInput;
    private readonly List<LegacyStroke> _pendingLegacy = new();
    private readonly List<RecorderRawInputService.PhysicalStroke> _pendingPhysical = new();
    public IntPtr OwnerWindowHandle { get; set; }
    public Action<string>? ReportRawInputWarning { get; set; }

    public GroupShortcutDialog(GroupShortcut? shortcut, Func<GroupShortcut?, bool, string?> tryAssign,
        Func<string?> suspend, Func<string?> resume, Func<GroupShortcut, string?> findOwner, string? initialError)
    {
        InitializeComponent();
        _tryAssign = tryAssign;
        _suspend = suspend;
        _resume = resume;
        _findOwner = findOwner;
        _candidate = shortcut;
        UpdateField();
        ShowError(initialError);
        Opened += (_, _) => OpenRawRecorder();
        Closing += (_, _) =>
        {
            StopRecording(cancel: true);
            _rawInput?.Dispose();
            _rawInput = null;
        };
    }

    private void OpenRawRecorder()
    {
        try
        {
            _rawInput = new RecorderRawInputService(OwnerWindowHandle, OnRawInput, error =>
            {
                StopRecording(cancel: true);
                ShowError(error);
                ReportRawInputWarning?.Invoke(error);
            });
        }
        catch (Exception ex)
        {
            RecorderButton.IsEnabled = false;
            ShowError("Physical keyboard recording is unavailable: " + ex.Message);
        }
    }

    private void RecorderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        if (_rawInput is null)
        {
            ShowError("Physical keyboard recording is unavailable.");
            return;
        }
        if (!RecorderButton.Focus(FocusState.Programmatic))
        {
            ShowError("The shortcut field needs keyboard focus to record.");
            return;
        }
        var error = _suspend();
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        _beforeRecording = _candidate;
        _rawInput.Reset(); // Begin with fresh physical presses; never guess held state.
        _pendingLegacy.Clear();
        _pendingPhysical.Clear();
        _capturedKeyToSuppress = 0;
        _recording = true;
        IsPrimaryButtonEnabled = false;
        ShowError(null);
        UpdateModifiers();
    }

    private void RecorderButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            if (_capturedKeyToSuppress == (uint)e.OriginalKey) e.Handled = true;
            return;
        }
        e.Handled = true; // Keep arrows/Alt/Enter from invoking dialog controls.
        var key = (uint)e.OriginalKey;
        if (key == 0x1B && !Down(0x11) && !Down(0x10) && !Down(0x12))
        {
            _capturedKeyToSuppress = key;
            StopRecording(cancel: true);
            return;
        }
        if (e.KeyStatus.WasKeyDown) return;
        if (IsModifier(key))
        {
            // Raw Input owns physical modifier state, including synthetic-release filtering.
            if (Down(0x5B) || Down(0x5C)) ShowError("Windows-key shortcuts are reserved and cannot be assigned.");
            return;
        }
        if (Down(0x5B) || Down(0x5C))
        {
            ShowError("Windows-key shortcuts are reserved and cannot be assigned.");
            return;
        }
        _pendingLegacy.Add(new LegacyStroke(key, Down(0x11), Down(0x10), Down(0x12),
            e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey));
        MatchInputs();
    }

    private readonly record struct LegacyStroke(uint Key, bool Control, bool Shift, bool Alt,
        uint ScanCode, bool IsExtended);

    private void OnRawInput(RecorderRawInputService.PhysicalStroke input)
    {
        if (!_recording) return;
        if (input.IsModifier)
        {
            UpdateModifiers();
            return;
        }
        if (!input.IsDown) return;
        if (input.WindowsHeld)
        {
            ShowError("Windows-key shortcuts are reserved and cannot be assigned.");
            return;
        }
        _pendingPhysical.Add(input);
        MatchInputs();
    }

    private void MatchInputs()
    {
        // Pair physical and translated data by scan/extended identity, accepting
        // either delivery order. Only this method creates a candidate.
        for (var legacyIndex = 0; legacyIndex < _pendingLegacy.Count; legacyIndex++)
        {
            var legacy = _pendingLegacy[legacyIndex];
            var physicalIndex = _pendingPhysical.FindIndex(raw =>
                raw.ScanCode == legacy.ScanCode && raw.IsExtended == legacy.IsExtended);
            if (physicalIndex < 0) continue;
            var physical = _pendingPhysical[physicalIndex];
            _pendingPhysical.RemoveAt(physicalIndex);
            _pendingLegacy.RemoveAt(legacyIndex);
            var shortcut = new GroupShortcut(legacy.Key, legacy.Control, legacy.Shift, legacy.Alt)
            {
                PhysicalScanCode = physical.ScanCode,
                PhysicalIsExtendedKey = physical.IsExtended,
                PhysicalModifiers = physical.Modifiers
            };
            if (shortcut.ValidationError is string error)
            {
                RecorderText.Text = shortcut.Label;
                ShowError(error);
                return;
            }
            _candidate = shortcut;
            _capturedKeyToSuppress = legacy.Key;
            StopRecording(cancel: false);
            return;
        }
        if (_pendingLegacy.Count > 8 || _pendingPhysical.Count > 8)
        {
            StopRecording(cancel: true);
            ShowError("Physical and translated keyboard events could not be matched. Release all keys and try recording again.");
        }
    }
    private void RecorderButton_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_capturedKeyToSuppress == (uint)e.OriginalKey)
        {
            // Enter/Space release must not activate the field and restart
            // recording immediately after their key-down was captured.
            e.Handled = true;
            _capturedKeyToSuppress = 0;
            return;
        }
        if (!_recording) return;
        e.Handled = true;
    }

    private void RecorderButton_LostFocus(object sender, RoutedEventArgs e) => StopRecording(cancel: true);

    private void UpdateModifiers()
    {
        var modifiers = _rawInput?.Modifiers ?? 0;
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if (Down(0x5B) || Down(0x5C)) parts.Add("Win (unsupported)");
        RecorderText.Text = parts.Count == 0 ? "Press a shortcut…" : string.Join(" + ", parts);
    }

    private void StopRecording(bool cancel)
    {
        if (!_recording) return;
        _recording = false;
        _pendingLegacy.Clear();
        _pendingPhysical.Clear();
        if (cancel)
        {
            _candidate = _beforeRecording;
        }
        UpdateField();
        ShowError(_resume());
    }

    private void UpdateField()
    {
        RecorderText.Text = _candidate?.Label ?? "Click to record";
        IsPrimaryButtonEnabled = _candidate is not null;
        UpdateWarning();
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_recording || _candidate is null)
        {
            ShowError("Record a complete shortcut, or use Clear to remove the assignment.");
            args.Cancel = true;
            return;
        }
        var owner = _findOwner(_candidate);
        if (owner is not null)
        {
            _pendingReassignment = _candidate;
            ReassignTextBlock.Text = $"{_candidate.Label} belongs to '{owner}'. Reassign it here and leave '{owner}' unbound?";
            ReassignPanel.Visibility = Visibility.Visible;
            RecorderButton.IsEnabled = false;
            IsPrimaryButtonEnabled = false;
            ShowError(null);
            args.Cancel = true;
            return;
        }
        Apply(_candidate, args);
    }

    private void ClearButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        StopRecording(cancel: true);
        CancelReassignment();
        Apply(null, args);
    }

    private void Apply(GroupShortcut? shortcut, ContentDialogButtonClickEventArgs args)
    {
        var error = _tryAssign(shortcut, false);
        ShowError(error);
        args.Cancel = error is not null;
    }

    private void ReassignButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingReassignment is null) return;
        var error = _tryAssign(_pendingReassignment, true);
        CancelReassignment();
        ShowError(error);
        if (error is null) Hide();
    }

    private void KeepAssignmentsButton_Click(object sender, RoutedEventArgs e) => CancelReassignment();

    private void CancelReassignment()
    {
        _pendingReassignment = null;
        ReassignPanel.Visibility = Visibility.Collapsed;
        RecorderButton.IsEnabled = true;
        UpdateField();
    }

    private void UpdateWarning()
    {
        TranslationInfoTextBlock.Text = _candidate?.TranslationWarning ?? "";
        TranslationInfoTextBlock.Visibility = string.IsNullOrWhiteSpace(TranslationInfoTextBlock.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        WarningTextBlock.Text = _candidate?.Warning ?? "";
        WarningTextBlock.Visibility = string.IsNullOrWhiteSpace(WarningTextBlock.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowError(string? error)
    {
        ErrorTextBlock.Text = error ?? "";
        ErrorTextBlock.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
