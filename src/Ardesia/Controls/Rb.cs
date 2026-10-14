using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ardesia.Controls;

/// <summary>Proprietà associata per l'etichetta dei pulsanti della barra multifunzione.</summary>
public static class Rb
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached("Label", typeof(string), typeof(Rb), new PropertyMetadata(""));

    public static string GetLabel(DependencyObject o) => (string)o.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject o, string value) => o.SetValue(LabelProperty, value);
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
