using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Linq;
using uchat.Services;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient? _network;
        private int _currentRoomId = 0;

        public ObservableCollection<MessageDto> ChatMessages { get; set; } = new ObservableCollection<MessageDto>();
        public ObservableCollection<ChatInfoDto> ChatList { get; set; } = new ObservableCollection<ChatInfoDto>();

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
            if (this.WindowState == WindowState.Normal)
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
    }
}