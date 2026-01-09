using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RealTimeTranslator.UI.Converters;

/// <summary>
/// 画面高さと下部マージン（%）からThicknessを生成するコンバーター
/// </summary>
public sealed class BottomMarginPercentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return new Thickness(0);
        }

        if (values[0] is not double percent || values[1] is not double height)
        {
            return new Thickness(0);
        }

        var bottomMargin = Math.Max(0, height * percent / 100.0);
        return new Thickness(0, 0, 0, bottomMargin);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
