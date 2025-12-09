using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace uchat
{
    public class UserIdToColorConverter : IValueConverter
    {
        private static readonly Color[] UserColors = new Color[]
        {
            Color.FromRgb(240, 244, 248), // Light blue-gray
            Color.FromRgb(245, 240, 232), // Light beige
            Color.FromRgb(240, 248, 240), // Light green
            Color.FromRgb(248, 240, 248), // Light pink
            Color.FromRgb(240, 248, 248), // Light cyan
            Color.FromRgb(248, 248, 240), // Light yellow
            Color.FromRgb(245, 245, 250), // Light purple-gray
            Color.FromRgb(250, 245, 240), // Light peach
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int userId)
            {
                int colorIndex = Math.Abs(userId) % UserColors.Length;
                return new SolidColorBrush(UserColors[colorIndex]);
            }
            return new SolidColorBrush(UserColors[0]);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

