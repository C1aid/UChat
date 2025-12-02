using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using uchat.Services;
using Uchat.Shared.DTOs;

namespace uchat.Views
{
    public partial class LoginWindow : Window
    {
        private NetworkClient _network;
        private bool _isLoginSuccessful = false;

        public LoginWindow()
        {
            InitializeComponent();
            _network = new NetworkClient();

            Loaded += (s, e) => UsernameBox.Focus();
            UsernameBox.KeyDown += HandleEnterKey;
            PasswordBox.KeyDown += HandleEnterKey;
        }

        private void HandleEnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                LoginButton_Click(sender, e);
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ShowError("Please fill in all fields");
                return;
            }

            LoginButton.Content = "Signing in...";
            LoginButton.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;

            try
            {
                bool connected = await _network.ConnectAsync(App.ServerIp, App.ServerPort);

                if (!connected)
                {
                    ShowError("Failed to connect to server");
                    LoginButton.Content = "Log in";
                    LoginButton.IsEnabled = true;
                    return;
                }

                bool sent = await _network.SendMessageAsync($"/login {username} {password}");
                if (!sent)
                {
                    ShowError("Failed to send login request");
                    _network.Disconnect();
                    LoginButton.Content = "Log in";
                    LoginButton.IsEnabled = true;
                    return;
                }

                string? response = await _network.ReceiveMessageAsync(5000);

                if (string.IsNullOrEmpty(response))
                {
                    ShowError("No response from server");
                    _network.Disconnect();
                    LoginButton.Content = "Log in";
                    LoginButton.IsEnabled = true;
                    return;
                }

                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(response);

                if (apiResponse?.Success == true)
                {
                    _isLoginSuccessful = true;
                    _network.StartBackgroundListening();

                    Dispatcher.Invoke(() =>
                    {
                        var mainWindow = new MainWindow(_network);
                        mainWindow.Show();
                        this.Hide();
                    });
                }
                else
                {
                    ShowError(apiResponse?.Message ?? "Invalid username or password");
                    _network.Disconnect();
                    LoginButton.Content = "Log in";
                    LoginButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error: {ex.Message}");
                _network?.Disconnect();
                LoginButton.Content = "Log in";
                LoginButton.IsEnabled = true;
            }
        }

        private void SignUpLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var registerWindow = new RegisterWindow();
            registerWindow.Show();
            this.Close();
        }

        private void ShowError(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ErrorText.Text = message;
                ErrorText.Visibility = Visibility.Visible;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_isLoginSuccessful)
            {
                _network?.Disconnect();
            }
            base.OnClosed(e);
        }
    }
}