using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RbsSipFone.ViewModels;

namespace RbsSipFone.Views;

/// <summary>true → Visible; mit Parameter "invert" umgekehrt.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Leerer String / null → Collapsed.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value == null || value is string s && string.IsNullOrWhiteSpace(s) || value is int i && i == 0;
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Registrierungszustand → Statusfarbe.</summary>
public sealed class RegistrationBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is RegistrationState state
            ? state switch
            {
                RegistrationState.Registered => "Success",
                RegistrationState.Registering => "Orange",
                RegistrationState.Failed => "Danger",
                _ => "LineStrong"
            }
            : "LineStrong";
        return Application.Current.Resources[key] as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
