using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using uchat.Models;
using uchat.Services;
using Uchat.Shared.DTOs;

namespace uchat
{
    public partial class LoginWindow : Window
    {
        private readonly NetworkClient _networkClient;

        public LoginWindow()
        {
            InitializeComponent();
            _networkClient = new NetworkClient();
            
            ClearUserSession();
            
            Loaded += (s, e) => UsernameBox.Focus();
            UsernameBox.KeyDown += TextBox_KeyDown;
            PasswordBox.KeyDown += PasswordBox_KeyDown;
        }

        private void ClearUserSession()
        {
            try
            {
                var session = UserSession.Current;
                session.UserId = 0;
                session.Username = string.Empty;
                session.DisplayName = string.Empty;
                session.ProfileInfo = string.Empty;
                session.Avatar = null;
                session.Theme = "Latte";
            }
            catch { }
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PasswordBox.Focus();
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                LoginButton_Click(sender, e);
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();
            
            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ShowError("Please enter username and password");
                return;
            }

            LoginButton.Content = "Signing in...";
            LoginButton.IsEnabled = false;

            try
            {
                string serverIp = App.ServerIp;
                int serverPort = App.ServerPort;

                bool connected = await _networkClient.ConnectAsync(serverIp, serverPort, username, password);
                if (!connected)
                {
                    ShowError("Failed to connect to server");
                    LoginButton.Content = "Sign In";
                    LoginButton.IsEnabled = true;
                    return;
                }

                _networkClient.SaveCredentials(username, password);
                _networkClient.EnableAutoReconnect(true);

                await _networkClient.SendMessageAsync($"/login {username} {password}");

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                ApiResponse? apiResponse = null;
                string? response = null;
                int attempts = 0;
                const int maxAttempts = 10;
                
                while (attempts < maxAttempts)
                {
                    response = await _networkClient.ReceiveMessageAsync(5000);

                if (string.IsNullOrEmpty(response))
                    {
                        attempts++;
                        if (attempts >= maxAttempts)
                {
                    ShowError("No response from server");
                            LoginButton.Content = "Sign In";
                            LoginButton.IsEnabled = true;
                    return;
                }
                        continue;
                    }

                    try
                    {
                        apiResponse = JsonSerializer.Deserialize<ApiResponse>(response, options);
                        
                        if (apiResponse != null)
                        {
                            if (apiResponse.Message == "Welcome to Uchat! Use /help for commands")
                            {
                                attempts++;
                                continue;
                            }
                            
                            if (apiResponse.Success)
                            {
                                var loginProfile = apiResponse.GetData<UserProfileDto>();
                                if (loginProfile != null && loginProfile.Id > 0 && !string.IsNullOrEmpty(loginProfile.Username))
                                {
                                    break;
                                }
                            }
                            
                            if (!apiResponse.Success && (apiResponse.Message.Contains("Login") || apiResponse.Message.Contains("Invalid")))
                            {
                                break;
                            }
                        }
                        
                        attempts++;
                    }
                    catch
                    {
                        attempts++;
                    }
                }

                if (apiResponse == null || !apiResponse.Success)
                {
                    ShowError(apiResponse?.Message ?? "Login failed");
                    LoginButton.Content = "Sign In";
                    LoginButton.IsEnabled = true;
                    return;
                }

                var userSession = new UserSession();
                UserProfileDto? userProfile = null;

                try
                {
                    userProfile = apiResponse.GetData<UserProfileDto>();
                
                    if (userProfile == null && apiResponse.Data is System.Text.Json.JsonElement dataElement)
                {
                    try
                    {
                        var jsonText = dataElement.GetRawText();
                        userProfile = JsonSerializer.Deserialize<UserProfileDto>(jsonText, options);
                    }
                    catch
                    {
                    }
                }
                    
                    if (userProfile == null && apiResponse.Data is UserProfileDto directProfile)
                    {
                        userProfile = directProfile;
                    }
                    
                    if (userProfile == null && apiResponse.Data is string dataString)
                    {
                        try
                        {
                            userProfile = JsonSerializer.Deserialize<UserProfileDto>(dataString, options);
                }
                        catch
                {
                        }
                    }
                    
                    if (userProfile == null && apiResponse.Data is System.Text.Json.JsonElement dataElement2)
                    {
                        try
                        {
                            var json = dataElement2.GetRawText();
                            var jsonDoc = JsonDocument.Parse(json);
                            var root = jsonDoc.RootElement;

                            int userId = 0;
                            if (root.TryGetProperty("Id", out var idElement))
                                userId = idElement.GetInt32();
                            else if (root.TryGetProperty("userId", out var userIdElement))
                                userId = userIdElement.GetInt32();
                            else if (root.TryGetProperty("UserId", out var userIdElement2))
                                userId = userIdElement2.GetInt32();

                            if (userId > 0)
                            {
                                userSession.UserId = userId;
                                
                                if (root.TryGetProperty("Username", out var usernameElement) || root.TryGetProperty("username", out usernameElement))
                                userSession.Username = usernameElement.GetString() ?? username;

                                if (root.TryGetProperty("DisplayName", out var displayNameElement) || root.TryGetProperty("displayName", out displayNameElement))
                                userSession.DisplayName = displayNameElement.GetString() ?? "";
                            
                                if (root.TryGetProperty("ProfileInfo", out var profileInfoElement) || root.TryGetProperty("profileInfo", out profileInfoElement))
                                userSession.ProfileInfo = profileInfoElement.GetString() ?? "";
                            
                                if (root.TryGetProperty("Theme", out var themeElement) || root.TryGetProperty("theme", out themeElement))
                                userSession.Theme = themeElement.GetString() ?? "Latte";
                                
                                if (root.TryGetProperty("Avatar", out var avatarElement) || root.TryGetProperty("avatar", out avatarElement))
                                {
                                    if (avatarElement.ValueKind == System.Text.Json.JsonValueKind.String)
                                    {
                                        var avatarBase64 = avatarElement.GetString();
                                        if (!string.IsNullOrEmpty(avatarBase64))
                                        {
                                            try
                                            {
                                                userSession.Avatar = Convert.FromBase64String(avatarBase64);
                                            }
                                            catch { }
                                        }
                                    }
                                    else if (avatarElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        try
                                        {
                                            userSession.Avatar = JsonSerializer.Deserialize<byte[]>(avatarElement.GetRawText());
                        }
                        catch { }
                    }
                }

                                userProfile = new UserProfileDto
                                {
                                    Id = userSession.UserId,
                                    Username = userSession.Username,
                                    DisplayName = userSession.DisplayName,
                                    ProfileInfo = userSession.ProfileInfo,
                                    Theme = userSession.Theme,
                                    Avatar = userSession.Avatar
                                };
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
                
                if (userProfile != null)
                {
                    userSession.UserId = userProfile.Id;
                    userSession.Username = userProfile.Username ?? string.Empty;
                    userSession.DisplayName = userProfile.DisplayName ?? string.Empty;
                    userSession.ProfileInfo = userProfile.ProfileInfo ?? string.Empty;
                    userSession.Theme = userProfile.Theme ?? "Latte";
                    userSession.Avatar = userProfile.Avatar;
                }

                if (userSession.UserId == 0 || string.IsNullOrEmpty(userSession.Username))
                {
                    ShowError("Failed to retrieve user data. Please try again.");
                    LoginButton.Content = "Sign In";
                    LoginButton.IsEnabled = true;
                    return;
                }

                _networkClient.StartBackgroundListening();

                ApplyTheme(userSession.Theme);

                var mainWindow = new MainWindow(_networkClient, userSession);
                mainWindow.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                ShowError($"Login error: {ex.Message}");
            }
            finally
            {
                LoginButton.Content = "Sign In";
                LoginButton.IsEnabled = true;
            }
        }

        public void SetUsername(string username)
        {
            if (UsernameBox != null && !string.IsNullOrEmpty(username))
            {
                UsernameBox.Text = username;
            }
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            var registerWindow = new Views.RegisterWindow();
            registerWindow.Show();
            this.Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        private void ApplyTheme(string themeName)
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

