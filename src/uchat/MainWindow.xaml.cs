using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Linq;
using uchat.Services;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Threading;
using System.Threading.Tasks;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient? _network;
        private int _currentRoomId = 0;

        public ObservableCollection<MessageDto> ChatMessages { get; set; } = new ObservableCollection<MessageDto>();
        public ObservableCollection<ChatInfoDto> ChatList { get; set; } = new ObservableCollection<ChatInfoDto>();

       
        private bool _isManuallyMaximized = false;
        private double _restoreWidth;
        private double _restoreHeight;
        private double _restoreTop;
        private double _restoreLeft;
        private readonly SemaphoreSlim _mediaDownloadSemaphore = new(1, 1);


        public MainWindow(NetworkClient network)
        {
            InitializeComponent();
            SwitchTheme("Latte");
            DataContext = this;
            _network = network;
            if (_network == null)
            {
                Close();
                return;
            }

            _network.MessageReceived += OnMessageReceived;
            _network.ConnectionLost += OnConnectionLost;

            ChatsList.SelectionChanged += async (sender, e) =>
            {
                if (ChatsList.SelectedItem is ChatInfoDto selectedChat)
                {
                    await OpenChatAsync(selectedChat.Id);
                }
            };

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadChatsAsync();
        }

        private async Task LoadChatsAsync()
        {
            try
            {
                if (_network != null && _network.IsConnected)
                {
                    await _network.SendMessageAsync("/getchats");
                }
            }
            catch (Exception)
            {
            }
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            string username = NewChatUsername.Text.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                MessageBox.Show("Введите имя пользователя");
                return;
            }

            try
            {
                if (_network != null && _network.IsConnected)
                {
                    await _network.SendMessageAsync($"/chat {username}");
                    NewChatUsername.Clear();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка создания чата: {ex.Message}");
            }
        }

        private async Task OpenChatAsync(int roomId)
        {
            try
            {
                if (_network == null || !_network.IsConnected) return;

                await _network.SendMessageAsync($"/join {roomId}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка открытия чата: {ex.Message}");
            }
        }

        private void OnMessageReceived(string messageJson)
        {
            try
            {
                var response = JsonSerializer.Deserialize<ApiResponse>(messageJson);
                if (response == null) return;

                Dispatcher.Invoke(() =>
                {
                    ProcessApiResponse(response);
                });
            }
            catch (Exception)
            {
            }
        }

        private void ProcessApiResponse(ApiResponse response)
        {
            try
            {
                if (!response.Success) return;

                if (response.Message == "User chats" && response.Data is JsonElement chatsData)
                {
                    var chats = JsonSerializer.Deserialize<ChatInfoDto[]>(chatsData.GetRawText());
                    if (chats != null)
                    {
                        ChatList.Clear();
                        foreach (var chat in chats)
                        {
                            ChatList.Add(chat);
                        }

                        if (ChatList.Count > 0)
                        {
                            ChatsList.SelectedItem = ChatList[0];
                        }
                    }
                }
                else if ((response.Message.StartsWith("Chat started") || response.Message == "Joined chat room") &&
                         response.Data is JsonElement chatData)
                {
                    ProcessChatResponse(chatData);
                }
                else if (response.Message == "New message" && response.Data is JsonElement msgData)
                {
                    ProcessNewMessage(msgData);
                }
                else if (response.Data is JsonElement notificationData)
                {
                    var msgDto = JsonSerializer.Deserialize<MessageDto>(notificationData.GetRawText());
                    if (msgDto != null && msgDto.MessageType == MessageType.NewChatNotification) 
                    {
                        ProcessNewChatNotification(msgDto.ChatRoomId);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async void ProcessNewChatNotification(int newChatId)
        {
            await LoadChatsAsync();
        }

        private void ProcessChatResponse(JsonElement chatData)
        {
            try
            {
                var chatResponse = JsonSerializer.Deserialize<ChatResponseData>(chatData.GetRawText());
                if (chatResponse == null) return;

                _currentRoomId = chatResponse.RoomId;
                ChatMessages.Clear();

                if (chatResponse.History != null)
                {
                    foreach (var msg in chatResponse.History)
                    {
                        ChatMessages.Add(msg);
                        QueueAutoDownload(msg);
                    }

                    if (MessagesList.Items.Count > 0)
                    {
                        MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                    }
                }

                Title = $"Uchat - Чат с {chatResponse.TargetUser}";
                MessageInput.Focus();

                AddOrUpdateChatInList(chatResponse);
            }
            catch (Exception)
            {
            }
        }

        private class ChatResponseData
        {
            public int RoomId { get; set; }
            public string TargetUser { get; set; } = string.Empty;
            public int OtherUserId { get; set; }
            public MessageDto[] History { get; set; } = Array.Empty<MessageDto>();
        }

        private void ProcessNewMessage(JsonElement msgData)
        {
            try
            {
                var msgDto = JsonSerializer.Deserialize<MessageDto>(msgData.GetRawText());
                if (msgDto != null && msgDto.ChatRoomId == _currentRoomId)
                {
                    ChatMessages.Add(msgDto);
                    QueueAutoDownload(msgDto);
                    if (MessagesList.Items.Count > 0)
                    {
                        MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void AddOrUpdateChatInList(ChatResponseData chatData)
        {
            var existingChat = ChatList.FirstOrDefault(c => c.Id == chatData.RoomId);
            if (existingChat == null)
            {
                ChatList.Add(new ChatInfoDto
                {
                    Id = chatData.RoomId,
                    OtherUserId = chatData.OtherUserId,
                    OtherUsername = chatData.TargetUser,
                    Name = $"Private_{_currentRoomId}_{chatData.OtherUserId}",
                    DisplayName = chatData.TargetUser,
                    IsGroup = false,
                    Description = chatData.History?.LastOrDefault()?.Content ?? "Нет сообщений",
                    CreatedAt = DateTime.Now,
                    UnreadCount = 0,
                    LastMessage = chatData.History?.LastOrDefault()?.Content,
                    LastMessageTime = chatData.History?.LastOrDefault()?.SentAt
                });
            }
            else
            {
                existingChat.DisplayName = chatData.TargetUser;
                if (chatData.History?.Length > 0)
                {
                    existingChat.LastMessage = chatData.History.Last().Content;
                    existingChat.LastMessageTime = chatData.History.Last().SentAt;
                }
            }
        }

        private void OnConnectionLost()
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Потеряно соединение с сервером.");
            });
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendMessage();
            }
        }

        private async void SendMessage()
        {
            string text = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (_network == null || !_network.IsConnected)
            {
                MessageBox.Show("Нет соединения с сервером");
                return;
            }

            try
            {
                if (_currentRoomId == 0)
                {
                    MessageBox.Show("Сначала выберите чат из списка");
                    MessageInput.Clear();
                    return;
                }

                await _network.SendMessageAsync(text);
                MessageInput.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка отправки: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_network != null)
                {
                    _network.MessageReceived -= OnMessageReceived;
                    _network.ConnectionLost -= OnConnectionLost;
                    _network.Disconnect();
                }
            }
            catch
            {
            }

            base.OnClosed(e);
            Application.Current.Shutdown();
        }
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
                return;
            }
            if (!_isManuallyMaximized)
            {
                this.DragMove();
            }
        }

        public void SwitchTheme(string themeName)
        {
            var uri = new Uri($"Themes/{themeName}.xaml", UriKind.Relative);
            ResourceDictionary newTheme = new ResourceDictionary() { Source = uri };
            var mergedDicts = Application.Current.Resources.MergedDictionaries;
            mergedDicts.Clear();
            mergedDicts.Add(newTheme);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }


        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isManuallyMaximized)
            {
                this.Width = _restoreWidth;
                this.Height = _restoreHeight;
                this.Top = _restoreTop;
                this.Left = _restoreLeft;
                this.WindowState = WindowState.Normal;
                _isManuallyMaximized = false;
            }
            else
            {
                _restoreWidth = this.Width;
                _restoreHeight = this.Height;
                _restoreTop = this.Top;
                _restoreLeft = this.Left;
                this.Width = SystemParameters.WorkArea.Width;
                this.Height = SystemParameters.WorkArea.Height;
                this.Top = SystemParameters.WorkArea.Top;
                this.Left = SystemParameters.WorkArea.Left;

                this.WindowState = WindowState.Normal;
                
                _isManuallyMaximized = true;
            }
        }

        private async void SendFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_network == null || !_network.IsConnected || _currentRoomId == 0)
            {
                MessageBox.Show("Підключіться до сервера та виберіть чат.");
                return;
            }
            var openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "Images (*.png;*.jpg)|*.png;*.jpg|Audio (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*";
            
            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;
                
                MessageType messageType = GetMessageTypeFromFile(filePath); 

                var response = await _network.SendFileMessageAsync(_currentRoomId, filePath, messageType);

                if (!response.Success)
                {
                    MessageBox.Show($"Помилка відправки файлу: {response.Message}");
                }
                else if (response.Data != null)
                {
                    try
                    {
                        var dataElement = (JsonElement)response.Data;
                        var msgDto = JsonSerializer.Deserialize<MessageDto>(dataElement.GetRawText());
                        if (msgDto != null && msgDto.ChatRoomId == _currentRoomId)
                        {
                            msgDto.LocalFilePath = filePath;
                            ChatMessages.Add(msgDto);
                            if (MessagesList.Items.Count > 0)
                            {
                                MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                            }
                            RefreshMessagesView();
                        }
                    }
                    catch { }
                }
            }
        }


        private async void DownloadFileButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.Tag == null) return;
            
            string fileId = button.Tag.ToString()!;

            if (_network == null || !_network.IsConnected)
            {
                MessageBox.Show("Not connected to server");
                return;
            }

            string defaultFileName = "downloaded_file";
            
            if (button.DataContext is MessageDto msg && !string.IsNullOrEmpty(msg.FileName))
            {
                defaultFileName = msg.FileName;
            }

            string downloadFolder = EnsureDownloadDirectory();
            string sanitizedName = SanitizeFileName(defaultFileName);
            string savePath = Path.Combine(downloadFolder, sanitizedName);

            if (File.Exists(savePath))
            {
                if (button.DataContext is MessageDto alreadyDownloaded)
                {
                    alreadyDownloaded.LocalFilePath = savePath;
                    CollectionViewSource.GetDefaultView(ChatMessages)?.Refresh();
                }

                MessageBox.Show($"Файл уже сохранён:\n{savePath}", "Уже загружен", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (success, message) = await _network.DownloadFileAsync(fileId, savePath);

            if (success)
            {
                if (button.DataContext is MessageDto downloadedMessage)
                {
                    downloadedMessage.LocalFilePath = savePath;
                    RefreshMessagesView();
                }

                MessageBox.Show($"Файл сохранён в {savePath}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Помилка завантаження: {message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private MessageType GetMessageTypeFromFile(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            
            if (ext == ".jpg" || ext == ".png" || ext == ".gif")
            {
                return MessageType.Image;
            }
            if (ext == ".mp3" || ext == ".wav")
            {
                return MessageType.Audio;
            }
            return  MessageType.File; 
        }

        private string EnsureDownloadDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string downloadDir = Path.Combine(baseDir, "Node_Downloads");
            Directory.CreateDirectory(downloadDir);
            return downloadDir;
        }

        private string SanitizeFileName(string? originalFileName)
        {
            string fallback = "uchat_file";
            if (string.IsNullOrWhiteSpace(originalFileName))
            {
                return fallback;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var chunks = originalFileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries);
            string sanitized = string.Join("_", chunks);

            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = fallback;
            }

            return sanitized;
        }

        private void QueueAutoDownload(MessageDto message)
        {
            if (message == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureLocalMediaAsync(message);
                }
                catch
                {
                }
            });
        }

        private async Task EnsureLocalMediaAsync(MessageDto message)
        {
            if (message == null || _network == null || !_network.IsConnected)
                return;
            if (message.MessageType != MessageType.Image)
                return;
            if (string.IsNullOrEmpty(message.FileUrl))
                return;
            if (!string.IsNullOrEmpty(message.LocalFilePath) && File.Exists(message.LocalFilePath))
            {
                RefreshMessagesView();
                return;
            }

            string downloadFolder = EnsureDownloadDirectory();
            string sanitizedName = SanitizeFileName(message.FileName);
            string savePath = Path.Combine(downloadFolder, sanitizedName);

            if (File.Exists(savePath))
            {
                message.LocalFilePath = savePath;
                RefreshMessagesView();
                return;
            }

            await _mediaDownloadSemaphore.WaitAsync();
            try
            {
                if (File.Exists(savePath))
                {
                    message.LocalFilePath = savePath;
                    RefreshMessagesView();
                    return;
                }

                var (success, _) = await _network.DownloadFileAsync(message.FileUrl, savePath);
                if (success)
                {
                    message.LocalFilePath = savePath;
                    RefreshMessagesView();
                }
            }
            finally
            {
                _mediaDownloadSemaphore.Release();
            }
        }

        private void RefreshMessagesView()
        {
            Dispatcher.Invoke(() =>
            {
                CollectionViewSource.GetDefaultView(ChatMessages)?.Refresh();
            });
        }
    }
}