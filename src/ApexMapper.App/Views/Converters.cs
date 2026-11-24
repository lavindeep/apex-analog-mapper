using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApexMapper.App.Views;

/// <summary>Visible for a value that is there: not null, not an empty string, not false, not an empty list. <see cref="Invert"/> flips it.</summary>
public sealed class PresenceToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return present != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible on the page given as the parameter. <see cref="Invert"/> shows it on every other page instead.</summary>
public sealed class PageToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A fraction of a length: the first value is the fraction, the second the length, such as a bar's width.</summary>
public sealed class FractionOfLength : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double fraction, double length] ? fraction * length : 0d;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A curve's exponent as a slider position: 0 is straight, and each step either way doubles
/// or halves it, so eager and gentle get the same length of track. Back to two decimals.
/// </summary>
public sealed class ExponentToPosition : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double exponent && exponent > 0 ? Math.Log2(exponent) : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double position ? Math.Round(Math.Pow(2, position), 2) : 1d;
}
