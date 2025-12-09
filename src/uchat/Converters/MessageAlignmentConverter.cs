using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using uchat.Models;

namespace uchat
{
    public class MessageAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int userId && parameter is string alignment)
            {
                var currentUserId = UserSession.Current?.UserId ?? 0;
                bool isCurrentUser = userId == currentUserId;

                if (alignment == "ShowUsername")
                {
                    return isCurrentUser ? Visibility.Collapsed : Visibility.Visible;
                }
                
                if (alignment == "HideTime")
                {
                    return isCurrentUser ? Visibility.Visible : Visibility.Collapsed;
                }

                if (alignment == "ShowEditMenu")
                {
                    return isCurrentUser ? Visibility.Visible : Visibility.Collapsed;
                }

                if (isCurrentUser)
                {
                    if (alignment == "Right")
                        return Visibility.Visible;
                    return Visibility.Collapsed;
                }
                else
                {
                    if (alignment == "Left")
                        return Visibility.Visible;
                    return Visibility.Collapsed;
                }
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

