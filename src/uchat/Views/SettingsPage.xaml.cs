using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using uchat;
using uchat.Models;
using uchat.Services;
using Uchat.Shared.DTOs;

namespace uchat.Views
{
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        private readonly NetworkClient _network;
        private readonly UserSession _userSession;
        private byte[]? _newAvatar;
        private MainWindow? _mainWindow;

        public SettingsPage(NetworkClient network, UserSession userSession, MainWindow mainWindow)
        {
            InitializeComponent();
            _network = network;
            _userSession = userSession;
            _mainWindow = mainWindow;

            LoadUserData();
            UpdateAvatarDisplay();
        }

        private void LoadUserData()
        {
            DisplayNameBox.Text = _userSession.DisplayName;
            UsernameBox.Text = _userSession.Username;
            ProfileInfoBox.Text = _userSession.ProfileInfo;

            SetThemeSelection(_userSession.Theme);
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

                var selectedTheme = GetSelectedTheme();

                var updateRequest = new UpdateProfileRequest
                {
                    Username = newUsername,
                    DisplayName = newDisplayName,
                    ProfileInfo = ProfileInfoBox.Text.Trim(),
                    Theme = selectedTheme,
                    Avatar = _newAvatar
                };

                var response = await _network.UpdateProfileAsync(updateRequest);

                if (response == null)
                {
                    MessageBox.Show("Error saving: No response from server. Please check your connection.",
                                  "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    SaveButton.Content = "Save changes";
                    SaveButton.IsEnabled = true;
                    return;
                }

                if (response.Success == true)
                {
                    var updatedProfile = response.GetData<UserProfileDto>();
                    
                    if (updatedProfile == null && response.Data is JsonElement dataElement)
                    {
                        try
                        {
                            updatedProfile = JsonSerializer.Deserialize<UserProfileDto>(dataElement.GetRawText());
                        }
                        catch
                        {
                        }
                    }
                    
                    if (updatedProfile != null)
                    {
                        _userSession.UpdateFromUserProfileDto(updatedProfile);
                        UpdateAvatarDisplay();
                        _newAvatar = null;
                        
                        if (_mainWindow != null)
                        {
                            _mainWindow.SwitchTheme(_userSession.Theme);
                        }
                        
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window is LoginWindow || window is RegisterWindow)
                            {
                                continue;
                            }
                        }
                    }

                    MessageBox.Show("Profile updated successfully!", "Success",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var errorMessage = response.Message ?? "Unknown error";
                    MessageBox.Show($"Error saving: {errorMessage}",
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
            var firstConfirm = MessageBox.Show(
                "Are you sure you want to delete your account?\n\n" +
                "This action cannot be undone. All your data will be permanently deleted:\n" +
                "• Profile and settings\n" +
                "• All messages\n" +
                "• All chats\n" +
                "• Downloaded files\n\n" +
                "This action is irreversible!",
                "Delete Account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (firstConfirm != MessageBoxResult.Yes)
                return;

            var secondConfirm = MessageBox.Show(
                "WARNING! This is the final warning!\n\n" +
                "Do you really want to delete your account?\n" +
                "All data will be deleted forever and cannot be recovered.",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop);

            if (secondConfirm != MessageBoxResult.Yes)
                return;

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
                    _userSession.Theme = "Latte";

                    if (_network != null && _network.IsConnected)
                    {
                        _network.Disconnect();
                    }

                    MessageBox.Show(
                        "Account successfully deleted.\nThe application will be closed.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (_mainWindow != null)
                        {
                            _mainWindow.Close();
                        }

                        var windowsToClose = new List<Window>();
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (!(window is LoginWindow))
                            {
                                windowsToClose.Add(window);
                            }
                        }

                        foreach (var window in windowsToClose)
                        {
                            window.Close();
                        }

                        var loginWindow = new LoginWindow();
                        loginWindow.Show();
                    });
                }
                else
                {
                    MessageBox.Show(
                        $"Error deleting account: {response?.Message ?? "Unknown error"}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    
                    DeleteAccountButton.Content = "Delete account";
                    DeleteAccountButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"An error occurred: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                
                DeleteAccountButton.Content = "Delete account";
                DeleteAccountButton.IsEnabled = true;
            }
        }


        private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.IsChecked == true)
            {
                string selectedTheme = MapRadioToTheme(radioButton);
                
                if (_mainWindow != null)
                {
                    _mainWindow.SwitchTheme(selectedTheme);
                }
                
                _userSession.Theme = selectedTheme;
                
                try
                {
                    var updateRequest = new UpdateProfileRequest
                    {
                        Username = _userSession.Username,
                        DisplayName = _userSession.DisplayName,
                        ProfileInfo = _userSession.ProfileInfo,
                        Theme = selectedTheme,
                        Avatar = null
                    };
                    
                    _ = _network.UpdateProfileAsync(updateRequest);
                }
                catch
                {
                }
            }
        }

        private void EditName_Click(object sender, RoutedEventArgs e)
        {
            DisplayNameBox.Focus();
            DisplayNameBox.SelectAll();
        }

        private void EditUsername_Click(object sender, RoutedEventArgs e)
        {
            UsernameBox.Focus();
            UsernameBox.SelectAll();
        }

        private void EditProfileInfo_Click(object sender, RoutedEventArgs e)
        {
            ProfileInfoBox.Focus();
            ProfileInfoBox.SelectAll();
        }

        private void SetThemeSelection(string? theme)
        {
            var targetRadio = theme switch
            {
                "Latte" => LatteThemeRadio,
                "Matcha" => MatchaThemeRadio,
                "Acai" => AcaiThemeRadio,
                "Lugia" => AcaiThemeRadio,
                "EarlGrey" => EarlGreyThemeRadio,
                "MulledWine" => MulledWineThemeRadio,
                "Anchan" => AnchanThemeRadio,
                _ => null
            };

            if (targetRadio != null)
            {
                targetRadio.IsChecked = true;
            }
        }

        private string GetSelectedTheme()
        {
            return LatteThemeRadio.IsChecked == true ? "Latte"
                 : MatchaThemeRadio.IsChecked == true ? "Matcha"
                 : AcaiThemeRadio.IsChecked == true ? "Acai"
                 : EarlGreyThemeRadio.IsChecked == true ? "EarlGrey"
                 : MulledWineThemeRadio.IsChecked == true ? "MulledWine"
                 : "Anchan";
        }

        private string MapRadioToTheme(RadioButton radioButton)
        {
            return radioButton == LatteThemeRadio ? "Latte"
                 : radioButton == MatchaThemeRadio ? "Matcha"
                 : radioButton == AcaiThemeRadio ? "Acai"
                 : radioButton == EarlGreyThemeRadio ? "EarlGrey"
                 : radioButton == MulledWineThemeRadio ? "MulledWine"
                 : "Anchan";
        }
    }
}

