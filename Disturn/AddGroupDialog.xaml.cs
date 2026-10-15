using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace Disturn;

public sealed partial class AddGroupDialog : ContentDialog
{
    public string GroupName { get; private set; } = "";
    public IReadOnlyCollection<string> ExistingGroupNames { get; set; } = Array.Empty<string>();

    public AddGroupDialog()
    {
        InitializeComponent();
    }

    private void AddButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var groupName = GroupNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(groupName))
        {
            ShowValidationMessage("Enter a group name.", args);
            return;
        }

        if (ExistingGroupNames.Contains(groupName, StringComparer.OrdinalIgnoreCase))
        {
            ShowValidationMessage("A group with that name already exists.", args);
            return;
        }

        GroupName = groupName;
    }

    private void ShowValidationMessage(string message, ContentDialogButtonClickEventArgs args)
    {
        ValidationMessageTextBlock.Text = message;
        ValidationMessageTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        args.Cancel = true;
    }
}
