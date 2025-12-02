using System;
using System.Windows;
using System.Windows.Input;
using uchat.Services;
using System.Text.Json;
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
                Console.WriteLine($"[RegisterWindow] Attempting to connect to server...");
                bool connected = await _network.ConnectAsync(App.ServerIp, App.ServerPort);

                if (!connected)
                {
                    ShowError("Failed to connect to server");
                    return;
                }

                Console.WriteLine($"[RegisterWindow] Sending register command...");
                await _network.SendMessageAsync($"/register {username} {password}");

                Console.WriteLine($"[RegisterWindow] Waiting for server response...");
                string? response = await _network.ReceiveMessageAsync();

                if (string.IsNullOrEmpty(response))
                {
                    ShowError("No response from server");
                    _network.Disconnect();
                    return;
                }

                Console.WriteLine($"[RegisterWindow] Parsing server response...");
                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(response);

                if (apiResponse?.Success == true)
                {
                    Console.WriteLine($"[RegisterWindow] Registration successful!");

                    MessageBox.Show("Account created successfully! You can now sign in.",
                                  "Registration Successful",
                                  MessageBoxButton.OK,
                                  MessageBoxImage.Information);

                    var loginWindow = new LoginWindow();
                    loginWindow.Show();
                    this.Close();
                }
                else
                {
                    Console.WriteLine($"[RegisterWindow] Registration failed: {apiResponse?.Message}");
                    ShowError(apiResponse?.Message ?? "Registration error");
                    _network.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RegisterWindow] Exception: {ex.Message}");
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

        protected override void OnClosed(EventArgs e)
        {
            _network?.Disconnect();
            base.OnClosed(e);
        }
    }
}