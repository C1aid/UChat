using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using uchat.Models;
using uchat.Services;
using Uchat.Shared.DTOs;

namespace uchat.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly NetworkClient _network;
        private readonly UserSession _userSession;
        private readonly ObservableCollection<ChatInfoDto> _chatList;
        private byte[]? _newAvatar;
        private string _originalTheme;

        public SettingsWindow(NetworkClient network, UserSession userSession, ObservableCollection<ChatInfoDto> chatList)
        {
            InitializeComponent();
            _network = network;
            _userSession = userSession;
            _chatList = chatList;
            _originalTheme = userSession.Theme;

            LoadUserData();
            UpdateAvatarDisplay();
        }

        private void LoadUserData()
        {
            DisplayNameBox.Text = _userSession.DisplayName;
            UsernameBox.Text = _userSession.Username;
            ProfileInfoBox.Text = _userSession.ProfileInfo;

            if (_userSession.Theme == "Latte")
                LatteThemeRadio.IsChecked = true;
            else if (_userSession.Theme == "Matcha")
                MatchaThemeRadio.IsChecked = true;
            else if (_userSession.Theme == "OceanBreeze" || _userSession.Theme == "Lugia")
                OceanBreezeThemeRadio.IsChecked = true;
            else if (_userSession.Theme == "DeepSpace")
                DeepSpaceThemeRadio.IsChecked = true;
            else if (_userSession.Theme == "WarmGlow")
                WarmGlowThemeRadio.IsChecked = true;
            else if (_userSession.Theme == "NorthernCold")
                NorthernColdThemeRadio.IsChecked = true;
        }


        private void UpdateAvatarDisplay()
        {
            if (_userSession.Avatar != null && _userSession.Avatar.Length > 0)
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    using (var stream = new MemoryStream(_userSession.Avatar))
                    {
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = stream;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                    }

                    AvatarImage.ImageSource = bitmapImage;
                }
                catch (Exception)
                {
                }
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveButton.Content = "Saving...";
            SaveButton.IsEnabled = false;

            try
            {
                var newUsername = UsernameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(newUsername))
                {
                    MessageBox.Show("Username cannot be empty", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Warning);
                    SaveButton.Content = "Save changes";
                    SaveButton.IsEnabled = true;
                    return;
                }

                var newDisplayName = DisplayNameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(newDisplayName))
                {
                    MessageBox.Show("Name cannot be empty", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Warning);
                    SaveButton.Content = "Save changes";
                    SaveButton.IsEnabled = true;
                    return;
                }

                var updateRequest = new UpdateProfileRequest
                {
                    Username = newUsername,
                    DisplayName = newDisplayName,
                    ProfileInfo = ProfileInfoBox.Text.Trim(),
                    Theme = LatteThemeRadio.IsChecked == true ? "Latte" 
                           : MatchaThemeRadio.IsChecked == true ? "Matcha"
                           : OceanBreezeThemeRadio.IsChecked == true ? "OceanBreeze"
                           : DeepSpaceThemeRadio.IsChecked == true ? "DeepSpace"
                           : WarmGlowThemeRadio.IsChecked == true ? "WarmGlow"
                           : "NorthernCold",
                    Avatar = _newAvatar
                };

                var response = await _network.UpdateProfileAsync(updateRequest);

                if (response?.Success == true)
                {
                    var updatedProfile = response.GetData<UserProfileDto>();
                    
                    if (updatedProfile == null && response.Data is JsonElement dataElement)
                    {
                        updatedProfile = JsonSerializer.Deserialize<UserProfileDto>(dataElement.GetRawText());
                    }
                    
                    if (updatedProfile != null)
                    {
                        _userSession.UpdateFromUserProfileDto(updatedProfile);
                        UpdateAvatarDisplay();
                        _newAvatar = null;
                        
                        var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                        if (mainWindow != null)
                        {
                            mainWindow.SwitchTheme(_userSession.Theme);
                        }
                        
                        SwitchTheme(_userSession.Theme);
                    }

                    MessageBox.Show("Profile updated successfully!", "Success",
                                  MessageBoxButton.OK, MessageBoxImage.Information);

                    this.Close();
                }
                else
                {
                    MessageBox.Show($"Error saving: {response?.Message ?? "Unknown error"}",
                                  "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SaveButton.Content = "Save changes";
                SaveButton.IsEnabled = true;
            }
        }

        private void AvatarButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
                Title = "Select avatar"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var fileBytes = File.ReadAllBytes(openFileDialog.FileName);

                    if (fileBytes.Length > 2 * 1024 * 1024)
                    {
                        MessageBox.Show("File is too large. Maximum size is 2MB",
                                      "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _newAvatar = fileBytes;

                    var bitmapImage = new BitmapImage();
                    using (var stream = new MemoryStream(fileBytes))
                    {
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = stream;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                    }

                    AvatarImage.ImageSource = bitmapImage;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading image: {ex.Message}",
                                  "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to delete your account? This action cannot be undone.\nAll your data will be permanently deleted.",
                                       "Delete Account",
                                       MessageBoxButton.YesNoCancel,
                                       MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                DeleteAccountButton.Content = "Deleting...";
                DeleteAccountButton.IsEnabled = false;

                try
                {
                    var response = await _network.DeleteAccountAsync();

                    if (response?.Success == true)
                    {
                        _userSession.UserId = 0;
                        _userSession.Username = string.Empty;
                        _userSession.DisplayName = string.Empty;
                        _userSession.ProfileInfo = string.Empty;
                        _userSession.Avatar = null;

                        if (_network != null && _network.IsConnected)
                        {
                            _network.Disconnect();
                        }

                        MessageBox.Show("Account deleted successfully", "Success",
                                      MessageBoxButton.OK, MessageBoxImage.Information);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (Window window in Application.Current.Windows)
                            {
                                if (window is LoginWindow)
                                    continue;
                                window.Close();
                            }

                            new LoginWindow().Show();
                        });
                    }
                    else
                    {
                        MessageBox.Show($"Error deleting account: {response?.Message ?? "Unknown error"}",
                                      "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        DeleteAccountButton.Content = "Delete account";
                        DeleteAccountButton.IsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error: {ex.Message}", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    DeleteAccountButton.Content = "Delete account";
                    DeleteAccountButton.IsEnabled = true;
                }
            }
        }


        private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void WindowMinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        public void SwitchTheme(string themeName)
        {
            try
            {
                var uri = new Uri($"Themes/{themeName}.xaml", UriKind.Relative);
                ResourceDictionary newTheme = new ResourceDictionary() { Source = uri };
                var mergedDicts = Application.Current.Resources.MergedDictionaries;
                mergedDicts.Clear();
                mergedDicts.Add(newTheme);
            }
            catch { }
        }
    }
}