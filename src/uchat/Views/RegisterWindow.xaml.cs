using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using uchat.Models;
using uchat.Services;
using Uchat.Shared.DTOs;

namespace uchat.Views
{
    public partial class RegisterWindow : Window
    {
        private NetworkClient _network;

        public RegisterWindow()
        {
            InitializeComponent();
            _network = new NetworkClient();

            Loaded += (s, e) => NameBox.Focus();
            NameBox.KeyDown += HandleEnterKey;
            UsernameBox.KeyDown += HandleEnterKey;
            PasswordBox.KeyDown += HandleEnterKey;
        }

        private void HandleEnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RegisterButton_Click(sender, e);
            }
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ShowError("Please fill in all fields");
                return;
            }

            if (username.Length < 3)
            {
                ShowError("Username must be at least 3 characters long");
                return;
            }

            if (password.Length < 6)
            {
                ShowError("Password must be at least 6 characters long");
                return;
            }

            RegisterButton.Content = "Creating account...";
            RegisterButton.IsEnabled = false;

            try
            {
                bool connected = await _network.ConnectAsync(App.ServerIp, App.ServerPort);

                if (!connected)
                {
                    ShowError("Failed to connect to server");
                    return;
                }

                await _network.SendMessageAsync($"/register {username} {password} {name}");

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
                    response = await _network.ReceiveMessageAsync(5000);
                    
                    if (string.IsNullOrEmpty(response))
                    {
                        attempts++;
                        if (attempts >= maxAttempts)
                        {
                            ShowError("No response from server");
                            RegisterButton.Content = "Create account";
                            RegisterButton.IsEnabled = true;
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
                            
                            if (apiResponse.Success && (apiResponse.Message.Contains("Registration") || apiResponse.Message.Contains("registered")))
                            {
                                break;
                            }
                            
                            if (!apiResponse.Success && (apiResponse.Message.Contains("Registration") || apiResponse.Message.Contains("already exists") || apiResponse.Message.Contains("Username")))
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
                    ShowError(apiResponse?.Message ?? "Registration error");
                    RegisterButton.Content = "Create account";
                    RegisterButton.IsEnabled = true;
                    return;
                }

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
                    
                    if (userProfile == null && apiResponse.Data is System.Text.Json.JsonElement dataElement2)
                    {
                        try
                        {
                            var jsonDoc = JsonDocument.Parse(dataElement2.GetRawText());
                            var root = jsonDoc.RootElement;
                            
                            if (root.TryGetProperty("UserId", out var userIdElement) || root.TryGetProperty("Id", out userIdElement))
                            {
                                var userId = userIdElement.GetInt32();
                                var usernameFromData = root.TryGetProperty("Username", out var usernameElement) 
                                    ? usernameElement.GetString() 
                                    : username;
                                
                                userProfile = new UserProfileDto
                                {
                                    Id = userId,
                                    Username = usernameFromData ?? username,
                                    DisplayName = name,
                                    ProfileInfo = "",
                                    Theme = "Latte",
                                    Avatar = null
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

                _network.Disconnect();
                
                UserSession.Current.UserId = 0;
                UserSession.Current.Username = string.Empty;
                UserSession.Current.DisplayName = string.Empty;
                UserSession.Current.ProfileInfo = string.Empty;
                UserSession.Current.Avatar = null;
                UserSession.Current.Theme = "Latte";
                
                var loginWindow = new LoginWindow();
                loginWindow.SetUsername(username);
                loginWindow.Show();
                this.Close();
            }
            catch (System.IO.IOException ioEx) when (ioEx.Message.Contains("broken pipe") ||
                                                      ioEx.Message.Contains("connection reset"))
            {
                ShowError("Connection lost. Please try again.");
            }
            catch (Exception ex)
            {
                ShowError($"Registration error: {ex.Message}");
                _network.Disconnect();
            }
            finally
            {
                RegisterButton.Content = "Create account";
                RegisterButton.IsEnabled = true;
            }
        }

        private void SignInLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var loginWindow = new LoginWindow();
            loginWindow.Show();
            this.Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}