// Windows formatting adapter for IINA SidebarSliderView's precise indicator.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace Mani_App;

// IINA SidebarSliderView indicator uses the same precise value as its input.
public sealed class DelaySecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => DelayEdit.FormatSeconds(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
