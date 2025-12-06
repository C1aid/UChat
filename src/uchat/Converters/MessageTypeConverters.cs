using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Uchat.Shared.Enums;

namespace uchat.Converters
{
    public class TextMessageVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MessageType type)
            {
                return type == MessageType.Text ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ImageMessageVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MessageType type)
            {
                return type == MessageType.Image ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }


    public class FileMessageVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MessageType type)
            {
                return (type == MessageType.File || type == MessageType.Audio || type == MessageType.Video) 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long size)
            {
                if (size < 1024)
                    return $"{size} B";
                if (size < 1024 * 1024)
                    return $"{size / 1024.0:F1} KB";
                if (size < 1024 * 1024 * 1024)
                    return $"{size / (1024.0 * 1024):F1} MB";
                return $"{size / (1024.0 * 1024 * 1024):F2} GB";
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FileTypeIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MessageType type)
            {
                return type switch
                {
                    MessageType.Audio => "🎵",
                    MessageType.Video => "🎬",
                    MessageType.File => "📄",
                    _ => "📎"
                };
            }
            return "📎";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LocalFileVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                return File.Exists(path) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ImagePathToBitmapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && File.Exists(path))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                    return DependencyProperty.UnsetValue;
                }
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
