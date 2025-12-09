using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace uchat.Converters
{
    public class AvatarToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte[] avatarData && avatarData.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(avatarData))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = ms;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        var brush = new ImageBrush(bitmap);
                        brush.Stretch = Stretch.UniformToFill;
                        return brush;
                    }
                }
                catch
                {
                    return GetDefaultGradient();
                }
            }

            return GetDefaultGradient();
        }

        private Brush GetDefaultGradient()
        {
            try
            {
                var accentColor = (SolidColorBrush)System.Windows.Application.Current.Resources["AccentBrush"];
                var color = accentColor.Color;
                
                return new LinearGradientBrush(
                    color,
                    Color.FromArgb(255, 
                        (byte)Math.Min(255, color.R + 30), 
                        (byte)Math.Min(255, color.G + 30), 
                        (byte)Math.Min(255, color.B + 30)),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(1, 1));
            }
            catch
            {
                return new LinearGradientBrush(
                    Color.FromRgb(139, 115, 85),
                    Color.FromRgb(166, 139, 107),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(1, 1));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

