using System.IO;
using System.Windows.Media.Imaging;

namespace uchat.Helpers
{
    public static class ImageHelper
    {
        public static byte[]? LoadAndResizeImage(string filePath)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(filePath);
                
                image.DecodePixelWidth = 200; 
                
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();

                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));

                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public static string BytesToBase64(byte[] imageBytes)
        {
            return Convert.ToBase64String(imageBytes);
        }
    }
}