using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Simple
{
    public class NullToVisibilityConverter : MarkupExtension, IValueConverter
    {
        private static NullToVisibilityConverter? _converter;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (_converter == null)
                _converter = new NullToVisibilityConverter();
            return _converter;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var invert = false;
            if (parameter != null)
                invert = string.Equals(parameter.ToString(), "invert", StringComparison.OrdinalIgnoreCase);

            var isVisible = value != null;
            if (value is string strValue)
                isVisible = !string.IsNullOrEmpty(strValue);
            else if (value is TimeSpan ts)
                isVisible = ts != TimeSpan.Zero;

            if (invert)
                isVisible = !isVisible;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    public class BoolToVisibilityConverter : MarkupExtension, IValueConverter
    {
        private static BoolToVisibilityConverter? _converter;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (_converter == null)
                _converter = new BoolToVisibilityConverter();
            return _converter;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var invert = false;
            if (parameter != null)
                invert = string.Equals(parameter.ToString(), "invert", StringComparison.OrdinalIgnoreCase);

            var isVisible = value is bool bValue && bValue;
            if (invert)
                isVisible = !isVisible;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
