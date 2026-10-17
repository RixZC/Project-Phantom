using System;
using Microsoft.UI.Xaml.Data;

namespace Disturn;

/// <summary>Keeps each group header's native tooltip in sync with expansion.</summary>
public sealed class GroupExpansionToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "Hide applications in this group" : "Show applications in this group";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
