using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Windows.Data;
using System.Threading;
using uchat.Models;
using uchat.Services;
using uchat.Views;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient? _network;
        private readonly UserSession _userSession;
        private int _currentRoomId = 0;

        public ObservableCollection<MessageDto> ChatMessages { get; set; } = new ObservableCollection<MessageDto>();
        public ObservableCollection<ChatInfoDto> ChatList { get; set; } = new ObservableCollection<ChatInfoDto>();

        private bool _isManuallyMaximized = false;
        private double _restoreWidth;
        private double _restoreHeight;
        private double _restoreTop;
        private double _restoreLeft;
        private bool _isSendingMessage = false;
        private bool _isOpeningChat = false;
        private CancellationTokenSource? _openingChatCts;
        private CancellationTokenSource? _profileLoadCts;
        private readonly object _profileLoadLock = new object();
        private int _lastLoadedProfileUserId = 0;

        public MainWindow(NetworkClient network, UserSession userSession)
        {
            InitializeComponent();
            _userSession = userSession;
            _network = network;
            
            UserSession.Current = _userSession;

            SwitchTheme(_userSession.Theme);
            DataContext = this;

            if (MessageInput != null)
            {
                MessageInput.Text = "Send a message...";
                MessageInput.Foreground = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
            }

            ChatHeaderPanel.Visibility = Visibility.Collapsed;
            MessageInputPanel.Visibility = Visibility.Collapsed;
            UserProfilePanel.Visibility = Visibility.Collapsed;
            RightPanelColumn.Width = new GridLength(0);

            if (_network == null)
            {
                MessageBox.Show("Network client is null. Application will close.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            _network.MessageReceived += OnMessageReceived;
            _network.ConnectionLost += OnConnectionLost;

            ChatsList.SelectionChanged += async (sender, e) =>
            {
                if (SettingsView.Visibility == Visibility.Visible || ProfileView.Visibility == Visibility.Visible)
                {
                    ChatsList.SelectedItem = null;
                    return;
                }
                
                if (ChatsList.SelectedItem is ChatInfoDto selectedChat)
                {
                    await OpenChatAsync(selectedChat.Id);
                }
            };

            if (SearchChatBox != null)
            {
                SearchChatBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 153, 153));
                
                SearchChatBox.GotFocus += (s, e) =>
                {
                    if (SearchChatBox.Text == "Search")
                    {
                        SearchChatBox.Text = "";
                        SearchChatBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 53, 40));
                    }
                };

                SearchChatBox.LostFocus += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(SearchChatBox.Text))
                    {
                        SearchChatBox.Text = "Search";
                        SearchChatBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 153, 153));
                    }
                };

                SearchChatBox.TextChanged += (s, e) =>
                {
                    if (SearchChatBox.Text != "Search")
                    {
                        FilterChats(SearchChatBox.Text);
                    }
                    else
                    {
                        FilterChats("");
                    }
                };
            }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadChatsAsync();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
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
            catch { }
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
            catch { }
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddChatPanel != null)
            {
                if (AddChatPanel.Visibility == Visibility.Visible)
                {
                    string username = NewChatUsername?.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        MessageBox.Show("Please enter username");
                        return;
                    }

                    try
                    {
                        if (_network != null && _network.IsConnected)
                        {
                            await _network.SendMessageAsync($"/chat {username}");
                            NewChatUsername?.Clear();
                            AddChatPanel.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            MessageBox.Show("No connection to server");
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error creating chat: {ex.Message}");
                    }
                }
                else
                {
                    AddChatPanel.Visibility = Visibility.Visible;
                    if (NewChatUsername != null)
                    {
                        NewChatUsername.Focus();
                    }
                }
            }
        }

        private async Task OpenChatAsync(int roomId)
        {
            await Task.Yield();
            try
            {
                if (_isOpeningChat)
                {
                    return;
                }

                if (_network == null || !_network.IsConnected)
                {
                    MessageBox.Show("No connection to server");
                    return;
                }

                if (_currentRoomId == roomId)
                {
                    return;
                }

                _isOpeningChat = true;
                _openingChatCts?.Cancel();
                _openingChatCts = new CancellationTokenSource();
                var cts = _openingChatCts;

                try
                {
                    var chat = ChatList.FirstOrDefault(c => c.Id == roomId);
                    if (chat != null)
                    {
                        chat.UnreadCount = 0;
                        
                        _currentRoomId = roomId;
                        
                        lock (_profileLoadLock)
                        {
                            _profileLoadCts?.Cancel();
                            _lastLoadedProfileUserId = 0;
                        }
                        
                        ChatMessages.Clear();
                        if (ProfileName != null) ProfileName.Text = "";
                        if (ProfileUsername != null) ProfileUsername.Text = "";
                        if (ProfileInfo != null) ProfileInfo.Text = "";
                        if (ProfileAvatar != null) ProfileAvatar.Fill = new SolidColorBrush(Colors.Gray);
                        if (ChatHeaderAvatar != null) ChatHeaderAvatar.Fill = new SolidColorBrush(Colors.Gray);
                        
                        Title = $"Uchat - Chat with {chat.DisplayName ?? chat.OtherUsername ?? "Unknown"}";
                        if (ChatHeaderName != null)
                        {
                            ChatHeaderName.Text = chat.DisplayName ?? chat.OtherUsername ?? "Unknown";
                        }
                        ChatHeaderPanel.Visibility = Visibility.Visible;
                        MessageInputPanel.Visibility = Visibility.Visible;
                        if (UserProfilePanel != null)
                        {
                            UserProfilePanel.Visibility = Visibility.Visible;
                        }
                        RightPanelColumn.Width = new GridLength(320);
                        MainContentGrid.Margin = new Thickness(6, 12, 0, 12);
                        MessageInput.Focus();
                    }

                    _ = _network.SendMessageAsync($"/join {roomId}").ContinueWith(task =>
                    {
                        if (!task.Result)
                        {
                            _isOpeningChat = false;
                            _openingChatCts?.Cancel();
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(5000, cts.Token);
                            if (!cts.Token.IsCancellationRequested && _isOpeningChat)
                            {
                                _isOpeningChat = false;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening chat: {ex.Message}");
                    _isOpeningChat = false;
                    _openingChatCts?.Cancel();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening chat: {ex.Message}");
                _isOpeningChat = false;
                _openingChatCts?.Cancel();
            }
        }

        private void OnMessageReceived(string messageJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(messageJson))
                    return;

                ApiResponse? response = null;
                try
                {
                    response = JsonSerializer.Deserialize<ApiResponse>(messageJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException)
                {
                    return;
                }

                if (response == null) return;

                if (Dispatcher.CheckAccess())
                {
                    ProcessApiResponse(response);
                }
                else
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            ProcessApiResponse(response);
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }
        }

        private void ProcessApiResponse(ApiResponse response)
        {
            try
            {
                if (!response.Success)
                {
                    if (_isOpeningChat && (response.Message?.Contains("chat", StringComparison.OrdinalIgnoreCase) == true ||
                                          response.Message?.Contains("join", StringComparison.OrdinalIgnoreCase) == true))
                    if (_isOpeningChat && (response.Message?.Contains("chat", StringComparison.OrdinalIgnoreCase) == true ||
                                          response.Message?.Contains("join", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        _isOpeningChat = false;
                        _openingChatCts?.Cancel();
                    }
                    return;
                }

                if (response.Message == "User chats" && response.Data is JsonElement chatsData)
                {
                    ProcessChatListResponse(chatsData);
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
                else if (response.Message == "Profile updated" && response.Data is JsonElement profileData)
                {
                    ProcessProfileUpdated(profileData);
                }
                else if (response.Message == "Login successful" && response.Data is JsonElement loginData)
                {
                    ProcessLoginResponse(loginData);
                }
                else if (response.Message == "Chat deleted successfully" || response.Message == "Chat deleted")
                {
                    ProcessChatDeleted(response);
                }
                else if (response.Message == "Message updated" && response.Data is JsonElement updatedMsgData)
                {
                    ProcessMessageUpdated(updatedMsgData);
                }
                else if ((response.Message == "Message deleted" || response.Message == "Message deleted successfully") && response.Data is JsonElement deleteData)
                {
                    ProcessMessageDeleted(deleteData);
                }
            }
            catch
            {
            }
        }

        private void ProcessChatListResponse(JsonElement chatsData)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var chats = JsonSerializer.Deserialize<ChatInfoDto[]>(chatsData.GetRawText(), options);
                if (chats != null)
                {
                    ChatList.Clear();
                    foreach (var chat in chats)
                    {
                        ChatList.Add(chat);
                    }

                    var tasks = chats
                        .Where(chat => chat.Avatar == null && chat.OtherUserId > 0)
                        .Select(async chat =>
                        {
                            var profile = await LoadUserProfileForChatAsync(chat.OtherUserId);
                            if (profile != null && profile.Avatar != null)
                            {
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    var existingChat = ChatList.FirstOrDefault(c => c.Id == chat.Id);
                                    if (existingChat != null)
                                    {
                                        // Сохраняем UnreadCount перед обновлением
                                        int savedUnreadCount = existingChat.UnreadCount;
                                        
                                        existingChat.Avatar = profile.Avatar;
                                        
                                        // Восстанавливаем UnreadCount после обновления
                                        existingChat.UnreadCount = savedUnreadCount;
                                        
                                        // Принудительно обновляем отображение
                                        var index = ChatList.IndexOf(existingChat);
                                        if (index >= 0)
                                        {
                                            ChatList.RemoveAt(index);
                                            ChatList.Insert(index, existingChat);
                                        }
                                    }
                                });
                            }
                        });

                    _ = Task.Run(async () => await Task.WhenAll(tasks));
                }
            }
            catch { }
        }

        private void ProcessProfileUpdated(JsonElement profileData)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<UserProfileDto>(profileData.GetRawText());
                if (profile == null) return;

                if (profile.Id == _userSession.UserId)
                {
                    _userSession.UpdateFromUserProfileDto(profile);
                    SwitchTheme(_userSession.Theme);
                }
                
                UpdateChatListAvatar(profile.Id, profile.Avatar);
                
                if (_currentRoomId > 0)
                {
                    var currentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                    if (currentChat != null && currentChat.OtherUserId == profile.Id)
                    {
                        UpdateProfilePanel(profile);
                    }
                }
            }
            catch
            {
            }
        }

        private void ProcessLoginResponse(JsonElement loginData)
        {
            try
            {
                try
                {
                    var jsonDoc = JsonDocument.Parse(loginData.GetRawText());
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("UserId", out var userIdElement))
                        _userSession.UserId = userIdElement.GetInt32();

                    if (root.TryGetProperty("Username", out var usernameElement))
                        _userSession.Username = usernameElement.GetString() ?? "";

                    if (root.TryGetProperty("DisplayName", out var displayNameElement))
                        _userSession.DisplayName = displayNameElement.GetString() ?? "";

                    if (root.TryGetProperty("ProfileInfo", out var profileInfoElement))
                        _userSession.ProfileInfo = profileInfoElement.GetString() ?? "";

                    if (root.TryGetProperty("Theme", out var themeElement))
                        _userSession.Theme = themeElement.GetString() ?? "Latte";

                    if (root.TryGetProperty("Avatar", out var avatarElement))
                    {
                        if (avatarElement.ValueKind == JsonValueKind.String)
                        {
                            var base64String = avatarElement.GetString();
                            if (!string.IsNullOrEmpty(base64String))
                            {
                                try
                                {
                                    _userSession.Avatar = Convert.FromBase64String(base64String);
                                }
                                catch { }
                            }
                        }
                        else if (avatarElement.ValueKind == JsonValueKind.Array)
                        {
                            var bytes = new List<byte>();
                            foreach (var item in avatarElement.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Number)
                                {
                                    bytes.Add((byte)item.GetInt32());
                                }
                            }
                            if (bytes.Count > 0)
                            {
                                _userSession.Avatar = bytes.ToArray();
                            }
                        }
                    }

                    if (root.TryGetProperty("Chats", out var chatsElement))
                    {
                        var chats = JsonSerializer.Deserialize<ChatInfoDto[]>(chatsElement.GetRawText());
                        if (chats != null)
                        {
                            ChatList.Clear();
                            foreach (var chat in chats)
                            {
                                ChatList.Add(chat);
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        private void ProcessChatResponse(JsonElement chatData)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var chatResponse = JsonSerializer.Deserialize<ChatResponseData>(chatData.GetRawText(), options);
                if (chatResponse == null)
                {
                    return;
                }

                _currentRoomId = chatResponse.RoomId;
                _isOpeningChat = false;
                _openingChatCts?.Cancel();
                
                lock (_profileLoadLock)
                {
                    _profileLoadCts?.Cancel();
                    _lastLoadedProfileUserId = 0;
                }
                
                ChatMessages.Clear();
                
                if (ProfileName != null) ProfileName.Text = "";
                if (ProfileUsername != null) ProfileUsername.Text = "";
                if (ProfileInfo != null) ProfileInfo.Text = "";
                if (ProfileAvatar != null) ProfileAvatar.Fill = new SolidColorBrush(Colors.Gray);
                if (ChatHeaderAvatar != null) ChatHeaderAvatar.Fill = new SolidColorBrush(Colors.Gray);

                if (chatResponse.History != null && chatResponse.History.Length > 0)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var msg in chatResponse.History)
                        {
                            ChatMessages.Add(msg);
                        }
                        
                        if (MessagesList.Items.Count > 0)
                        {
                            MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                        }
                        
                        // Обновляем последнее сообщение в списке чатов
                        var chat = ChatList.FirstOrDefault(c => c.Id == chatResponse.RoomId);
                        if (chat != null)
                        {
                            // Ищем последнее текстовое сообщение в истории
                            var lastTextMessage = chatResponse.History
                                .Where(m => m.MessageType == MessageType.Text && !string.IsNullOrEmpty(m.Content))
                                .OrderByDescending(m => m.SentAt)
                                .FirstOrDefault();
                            
                            if (lastTextMessage != null)
                            {
                                chat.LastMessage = lastTextMessage.Content;
                                chat.LastMessageTime = lastTextMessage.SentAt;
                            }
                            else if (chatResponse.History.Length > 0)
                            {
                                // Если нет текстовых сообщений, берем последнее сообщение
                                var lastMessage = chatResponse.History.Last();
                                chat.LastMessageTime = lastMessage.SentAt;
                            }
                            
                            // Сохраняем текущий UnreadCount перед обновлением
                            int savedUnreadCount = chat.UnreadCount;
                            
                            // Сбрасываем счетчик непрочитанных при открытии чата
                            chat.UnreadCount = 0;
                            
                            // Принудительно обновляем отображение
                            var index = ChatList.IndexOf(chat);
                            if (index >= 0)
                            {
                                ChatList.RemoveAt(index);
                                ChatList.Insert(index, chat);
                            }
                        }
                    });
                    
                }

                Title = $"Uchat - Chat with {chatResponse.TargetUser}";
                
                int userIdToLoad = chatResponse.OtherUserId;
                
                if (userIdToLoad <= 0)
                {
                    var chatInList = ChatList.FirstOrDefault(c => c.Id == chatResponse.RoomId);
                    if (chatInList != null)
                    {
                        userIdToLoad = chatInList.OtherUserId;
                    }
                }
                
                var userId = userIdToLoad;
                
                if (ChatHeaderName != null)
                {
                    ChatHeaderName.Text = chatResponse.TargetUser;
                }
                
                ChatHeaderPanel.Visibility = Visibility.Visible;
                MessageInputPanel.Visibility = Visibility.Visible;
                if (UserProfilePanel != null)
                {
                    UserProfilePanel.Visibility = Visibility.Visible;
                }
                RightPanelColumn.Width = new GridLength(320);
                MainContentGrid.Margin = new Thickness(6, 12, 0, 12);
                
                MessageInput.Focus();
                
                    if (userId > 0)
                    {
                        _ = LoadUserProfileAsync(userId);
                        _ = Task.Run(() =>
                        {
                            Dispatcher.InvokeAsync(() => CalculateChatStatistics(userId));
                        });
                    }
                    else
                    {
                    if (UserProfilePanel != null)
                    {
                        UserProfilePanel.Visibility = Visibility.Visible;
                    }
                }

                _ = AddOrUpdateChatInList(chatResponse);
            }
            catch
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
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var msgDto = JsonSerializer.Deserialize<MessageDto>(msgData.GetRawText(), options);
                if (msgDto == null) return;

                if (msgDto.ChatRoomId == _currentRoomId)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        ChatMessages.Add(msgDto);
                        if (MessagesList.Items.Count > 0)
                        {
                            MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                        }
                    });
                    
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        var currentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                        if (currentChat != null)
                        {
                            // Сбрасываем счетчик непрочитанных для открытого чата
                            currentChat.UnreadCount = 0;
                            
                            // Обновляем последнее сообщение для текущего чата
                            if (msgDto.MessageType == MessageType.Text && !string.IsNullOrEmpty(msgDto.Content))
                            {
                                currentChat.LastMessage = msgDto.Content;
                                currentChat.LastMessageTime = msgDto.SentAt;
                                
                                // Перемещаем чат в начало списка при новом сообщении
                                var index = ChatList.IndexOf(currentChat);
                                if (index > 0)
                                {
                                    ChatList.Move(index, 0);
                                }
                                else if (index == 0)
                                {
                                    // Принудительно обновляем отображение, если чат уже на первом месте
                                    ChatList.RemoveAt(0);
                                    ChatList.Insert(0, currentChat);
                                }
                            }
                            
                            _ = Task.Run(() =>
                            {
                                Dispatcher.InvokeAsync(() => CalculateChatStatistics(currentChat.OtherUserId));
                            });
                            
                            if (msgDto.UserId != _userSession.UserId && _lastLoadedProfileUserId != currentChat.OtherUserId)
                            {
                                _ = LoadUserProfileAsync(currentChat.OtherUserId);
                            }
                        }
                    });
                }
                else
                {
                    UpdateChatFromMessage(msgDto);
                }
            }
            catch { }
        }

        private void UpdateChatFromMessage(MessageDto msgDto)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var chat = ChatList.FirstOrDefault(c => c.Id == msgDto.ChatRoomId);
                    if (chat != null)
                    {
                        // Обновляем последнее сообщение только для текстовых сообщений
                        if (msgDto.MessageType == MessageType.Text && !string.IsNullOrEmpty(msgDto.Content))
                        {
                            chat.LastMessage = msgDto.Content;
                            chat.LastMessageTime = msgDto.SentAt;
                        }
                        
                        // Увеличиваем счетчик непрочитанных, если это не текущий открытый чат и сообщение не от нас
                        if (msgDto.ChatRoomId != _currentRoomId && msgDto.UserId != _userSession.UserId)
                        {
                            // Увеличиваем счетчик непрочитанных
                            chat.UnreadCount = Math.Max(0, chat.UnreadCount) + 1;
                        }
                        
                        // Перемещаем чат в начало списка
                        var index = ChatList.IndexOf(chat);
                        if (index > 0)
                        {
                            ChatList.Move(index, 0);
                        }
                        else if (index == 0)
                        {
                            // Принудительно обновляем отображение, если чат уже на первом месте
                            var savedUnreadCount = chat.UnreadCount;
                            ChatList.RemoveAt(0);
                            ChatList.Insert(0, chat);
                            // Убеждаемся, что UnreadCount не потерялся
                            if (chat.UnreadCount != savedUnreadCount)
                            {
                                chat.UnreadCount = savedUnreadCount;
                            }
                        }
                    }
                    else
                    {
                        // Создаем новый чат, если его нет в списке
                        var displayName = msgDto.UserId == _userSession.UserId ? "Unknown" : msgDto.Username;
                        
                        var lastMessageText = msgDto.MessageType == MessageType.Text && !string.IsNullOrEmpty(msgDto.Content) 
                            ? msgDto.Content 
                            : "New message";
                        
                        var newChat = new ChatInfoDto
                        {
                            Id = msgDto.ChatRoomId,
                            DisplayName = displayName,
                            OtherUsername = displayName,
                            LastMessage = lastMessageText,
                            LastMessageTime = msgDto.SentAt,
                            UnreadCount = msgDto.UserId != _userSession.UserId ? 1 : 0,
                            IsGroup = false,
                            CreatedAt = DateTime.Now
                        };
                        
                        ChatList.Insert(0, newChat);
                    }
                }
                catch { }
            });
        }

        private async Task AddOrUpdateChatInList(ChatResponseData chatData)
        {
            try
            {
                var existingChat = ChatList.FirstOrDefault(c => c.Id == chatData.RoomId);
                if (existingChat == null)
                {
                    var profile = await LoadUserProfileForChatAsync(chatData.OtherUserId);
                    
                    ChatList.Add(new ChatInfoDto
                    {
                        Id = chatData.RoomId,
                        OtherUserId = chatData.OtherUserId,
                        OtherUsername = chatData.TargetUser,
                        Name = $"Private_{_currentRoomId}_{chatData.OtherUserId}",
                        DisplayName = chatData.TargetUser,
                        IsGroup = false,
                        Description = chatData.History?.LastOrDefault()?.Content ?? "No messages",
                        CreatedAt = DateTime.Now,
                        UnreadCount = 0,
                        LastMessage = chatData.History?.Where(m => m.MessageType == MessageType.Text && !string.IsNullOrEmpty(m.Content))
                            .OrderByDescending(m => m.SentAt)
                            .FirstOrDefault()?.Content ?? "No messages",
                        LastMessageTime = chatData.History?.LastOrDefault()?.SentAt ?? DateTime.Now,
                        Avatar = profile?.Avatar
                    });
                }
                else
                {
                    existingChat.DisplayName = chatData.TargetUser;
                    if (chatData.History?.Length > 0)
                    {
                        // Сохраняем UnreadCount перед обновлением
                        int savedUnreadCount = existingChat.UnreadCount;
                        
                        // Ищем последнее текстовое сообщение в истории
                        var lastTextMessage = chatData.History
                            .Where(m => m.MessageType == MessageType.Text && !string.IsNullOrEmpty(m.Content))
                            .OrderByDescending(m => m.SentAt)
                            .FirstOrDefault();
                        
                        if (lastTextMessage != null)
                        {
                            existingChat.LastMessage = lastTextMessage.Content;
                            existingChat.LastMessageTime = lastTextMessage.SentAt;
                        }
                        else
                        {
                            // Если нет текстовых сообщений, берем последнее сообщение для времени
                            var lastMessage = chatData.History.Last();
                            existingChat.LastMessageTime = lastMessage.SentAt;
                        }
                        
                        // Восстанавливаем UnreadCount после обновления
                        existingChat.UnreadCount = savedUnreadCount;
                        
                        // Принудительно обновляем отображение
                        var index = ChatList.IndexOf(existingChat);
                        if (index >= 0)
                        {
                            ChatList.RemoveAt(index);
                            ChatList.Insert(index, existingChat);
                        }
                    }
                    
                    if (existingChat.Avatar == null && chatData.OtherUserId > 0)
                    {
                        var profile = await LoadUserProfileForChatAsync(chatData.OtherUserId);
                        if (profile != null)
                        {
                            existingChat.Avatar = profile.Avatar;
                        }
                    }
                }
            }
            catch { }
        }

        private void OnConnectionLost()
        {
            if (_network != null && _network.IsConnected)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                MessageBox.Show("Connection to server lost.", "Connection Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Connection to server lost.", "Connection Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true; // Prevent adding new line in RichTextBox
                SendMessage();
            }
        }

        private void MessageInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (MessageInput != null && MessageInput.Text == "Send a message...")
            {
                MessageInput.Text = "";
                MessageInput.Foreground = new SolidColorBrush(Colors.Black);
            }
        }

        private void MessageInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MessageInput != null && string.IsNullOrWhiteSpace(MessageInput.Text))
            {
                MessageInput.Text = "Send a message...";
                MessageInput.Foreground = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
            }
        }

        private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MessageInput != null && MessageInput.Text.Contains("Send a message..."))
            {
                if (MessageInput.Text != "Send a message...")
                {
                    MessageInput.Text = MessageInput.Text.Replace("Send a message...", "");
                    MessageInput.Foreground = new SolidColorBrush(Colors.Black);
                }
            }
        }


        private async void SendMessage()
        {
            if (_isSendingMessage) return;
            
            if (MessageInput == null) return;
            
            string text = MessageInput.Text?.Trim() ?? "";
            
            if (string.IsNullOrEmpty(text) || text == "Send a message...")
            {
                return;
            }

            if (_network == null || !_network.IsConnected)
            {
                MessageBox.Show("No connection to server");
                return;
            }

            if (_currentRoomId == 0)
            {
                MessageBox.Show("Please select a chat from the list first");
                MessageInput.Text = "";
                MessageInput.Foreground = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
                MessageInput.Text = "Send a message...";
                return;
            }

            _isSendingMessage = true;
            try
            {
                var messageText = text;
                MessageInput.Text = "Send a message...";
                MessageInput.Foreground = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
                
                var sendTask = _network.SendMessageAsync(messageText);
                
                _ = Dispatcher.InvokeAsync(() =>
                {
                    var chat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                    if (chat != null)
                    {
                        chat.LastMessage = messageText;
                        chat.LastMessageTime = DateTime.Now;
                        
                        var index = ChatList.IndexOf(chat);
                        if (index > 0)
                        {
                            ChatList.Move(index, 0);
                        }
                    }
                });
                
                await sendTask;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending message: {ex.Message}");
            }
            finally
            {
                _isSendingMessage = false;
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
            catch { }

            base.OnClosed(e);
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
            try
            {
                var uri = new Uri($"Themes/{themeName}.xaml", UriKind.Relative);
                ResourceDictionary newTheme = new ResourceDictionary() { Source = uri };
                var mergedDicts = Application.Current.Resources.MergedDictionaries;
                mergedDicts.Clear();
                mergedDicts.Add(newTheme);
                
                _ = Task.Run(() =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window is MainWindow)
                            {
                                continue;
                            }
                            else if (window is LoginWindow || window is Views.RegisterWindow)
                            {
                                continue;
                            }
                            else if (window is Views.SettingsWindow settingsWindow)
                            {
                                try
                                {
                                    settingsWindow.SwitchTheme(themeName);
                                }
                                catch { }
                            }
                        }
                    });
                });
            }
            catch { }
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

        private void ChatsRadioButton_Click(object sender, RoutedEventArgs e)
        {
            ShowChatsPage();
        }

        private void ProfileRadioButton_Click(object sender, RoutedEventArgs e)
        {
            ShowProfilePage();
        }

        private void SettingsRadioButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsPage();
        }

        private void ShowChatsPage()
        {
            MessagesView.Visibility = Visibility.Visible;
            SettingsView.Visibility = Visibility.Collapsed;
            ProfileView.Visibility = Visibility.Collapsed;
            
            _currentRoomId = 0;
            
            lock (_profileLoadLock)
            {
                _profileLoadCts?.Cancel();
                _lastLoadedProfileUserId = 0;
            }
            ChatHeaderPanel.Visibility = Visibility.Collapsed;
            MessageInputPanel.Visibility = Visibility.Collapsed;
            UserProfilePanel.Visibility = Visibility.Collapsed;
            RightPanelColumn.Width = new GridLength(0);
            MainContentGrid.Margin = new Thickness(6, 12, 0, 12);
            ChatHeaderName.Text = "Select a chat";
            ChatMessages.Clear();
            
            if (ProfileName != null) ProfileName.Text = "";
            if (ProfileUsername != null) ProfileUsername.Text = "";
            if (ProfileInfo != null) ProfileInfo.Text = "";
            if (ProfileAvatar != null) ProfileAvatar.Fill = new SolidColorBrush(Colors.Gray);
            if (ChatHeaderAvatar != null) ChatHeaderAvatar.Fill = new SolidColorBrush(Colors.Gray);
        }

        private void ShowProfilePage()
        {
            MessagesView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            ProfileView.Visibility = Visibility.Visible;
            ChatHeaderPanel.Visibility = Visibility.Collapsed;
            MessageInputPanel.Visibility = Visibility.Collapsed;
            UserProfilePanel.Visibility = Visibility.Collapsed;
            RightPanelColumn.Width = new GridLength(0);
            MainContentGrid.Margin = new Thickness(6, 12, 12, 12);
        }

        private void ShowSettingsPage()
        {
            if (_network == null)
            {
                MessageBox.Show("Network client is not initialized");
                return;
            }

            try
            {
                MessagesView.Visibility = Visibility.Collapsed;
                SettingsView.Visibility = Visibility.Visible;
                ProfileView.Visibility = Visibility.Collapsed;
                ChatHeaderPanel.Visibility = Visibility.Collapsed;
                MessageInputPanel.Visibility = Visibility.Collapsed;
                UserProfilePanel.Visibility = Visibility.Collapsed;
                RightPanelColumn.Width = new GridLength(0);
                MainContentGrid.Margin = new Thickness(6, 12, 12, 12);
                ChatsList.SelectedItem = null;
                _currentRoomId = 0;
            
            lock (_profileLoadLock)
            {
                _profileLoadCts?.Cancel();
                _lastLoadedProfileUserId = 0;
            }

                if (SettingsFrame.Content == null)
                {
                    var settingsPage = new SettingsPage(_network, _userSession, this);
                    SettingsFrame.Content = settingsPage;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening settings: {ex.Message}");
            }
        }

        private void FilterChats(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText) || searchText == "Search")
            {
                ChatsList.ItemsSource = ChatList;
                return;
            }

            var filtered = ChatList.Where(chat =>
                chat.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                chat.LastMessage?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();

            ChatsList.ItemsSource = filtered;
        }

        private async Task LoadUserProfileAsync(int userId)
        {
            lock (_profileLoadLock)
            {
                _profileLoadCts?.Cancel();
                _profileLoadCts = new CancellationTokenSource();
            }

            var cts = _profileLoadCts;

            try
            {
                if (_network == null || !_network.IsConnected)
                {
                    return;
                }

                var response = await _network.GetUserProfileAsync(userId);
                
                if (cts?.Token.IsCancellationRequested == true)
                {
                    return;
                }

                if (response == null)
                {
                    return;
                }

                if (!response.Success)
                {
                    return;
                }

                if (response.Data is not JsonElement dataElement)
                {
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var profile = JsonSerializer.Deserialize<Uchat.Shared.DTOs.UserProfileDto>(dataElement.GetRawText(), options);
                if (profile == null)
                {
                    return;
                }
                
                if (cts?.Token.IsCancellationRequested == true)
                {
                    return;
                }

                var currentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                if (currentChat != null && currentChat.OtherUserId > 0 && currentChat.OtherUserId != profile.Id)
                {
                    return;
                }

                if (Dispatcher.CheckAccess())
                {
                    var uiCurrentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                    if (uiCurrentChat != null && uiCurrentChat.OtherUserId > 0 && uiCurrentChat.OtherUserId != profile.Id)
                    {
                        return;
                    }
                    
                    if (UserProfilePanel != null && UserProfilePanel.Visibility != Visibility.Visible)
                    {
                        UserProfilePanel.Visibility = Visibility.Visible;
                        if (RightPanelColumn != null)
                        {
                            RightPanelColumn.Width = new GridLength(320);
                        }
                    }
                    
                    var chatForCheck = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                    bool isOtherUser = chatForCheck != null && chatForCheck.OtherUserId > 0 && chatForCheck.OtherUserId == profile.Id;
                    
                    if (isOtherUser || profile.Id != _userSession.UserId)
                    {
                        _lastLoadedProfileUserId = profile.Id;
                        UpdateProfilePanel(profile);
                    }
                    else
                    {
                        if (ChatHeaderAvatar != null)
                        {
                            UpdateAvatar(ChatHeaderAvatar, profile.Avatar);
                        }
                        if (ChatHeaderName != null)
                        {
                            ChatHeaderName.Text = !string.IsNullOrWhiteSpace(profile.DisplayName) 
                                ? profile.DisplayName 
                                : profile.Username ?? "Unknown";
                        }
                    }
                }
                else
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        var uiCurrentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                        if (uiCurrentChat != null && uiCurrentChat.OtherUserId > 0 && uiCurrentChat.OtherUserId != profile.Id)
                        {
                            return;
                        }
                        
                        if (UserProfilePanel != null && UserProfilePanel.Visibility != Visibility.Visible)
                        {
                            UserProfilePanel.Visibility = Visibility.Visible;
                            if (RightPanelColumn != null)
                            {
                                RightPanelColumn.Width = new GridLength(320);
                            }
                        }
                        
                        var chatForCheck = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                        bool isOtherUser = chatForCheck != null && chatForCheck.OtherUserId > 0 && chatForCheck.OtherUserId == profile.Id;
                        
                        if (isOtherUser || profile.Id != _userSession.UserId)
                        {
                            _lastLoadedProfileUserId = profile.Id;
                            UpdateProfilePanel(profile);
                        }
                        else
                        {
                            if (ChatHeaderAvatar != null)
                            {
                                UpdateAvatar(ChatHeaderAvatar, profile.Avatar);
                            }
                            if (ChatHeaderName != null)
                            {
                                ChatHeaderName.Text = !string.IsNullOrWhiteSpace(profile.DisplayName) 
                                    ? profile.DisplayName 
                                    : profile.Username ?? "Unknown";
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }

        private async Task<Uchat.Shared.DTOs.UserProfileDto?> LoadUserProfileForChatAsync(int userId)
        {
            try
            {
                if (_network == null || !_network.IsConnected)
                    return null;

                var response = await _network.GetUserProfileAsync(userId);
                if (response?.Success == true && response.Data is JsonElement dataElement)
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    return JsonSerializer.Deserialize<Uchat.Shared.DTOs.UserProfileDto>(dataElement.GetRawText(), options);
                }
            }
            catch
            {
            }
            return null;
        }

        private void UpdateProfilePanel(Uchat.Shared.DTOs.UserProfileDto profile)
        {
            try
            {
                var currentChat = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                if (currentChat != null && currentChat.OtherUserId > 0 && currentChat.OtherUserId != profile.Id)
                {
                    return;
                }

                if (UserProfilePanel == null || ProfileName == null || ProfileUsername == null || 
                    ProfileInfo == null || ProfileAvatar == null || ChatHeaderAvatar == null || ChatHeaderName == null)
                {
                    return;
                }
                
                if (UserProfilePanel.Visibility != Visibility.Visible)
                {
                    UserProfilePanel.Visibility = Visibility.Visible;
                    if (RightPanelColumn != null)
                    {
                        RightPanelColumn.Width = new GridLength(320);
                    }
                }

                var displayName = !string.IsNullOrWhiteSpace(profile.DisplayName) 
                    ? profile.DisplayName 
                    : profile.Username ?? "Unknown";
                ProfileName.Text = displayName;
                
                var username = $"@{profile.Username ?? "unknown"}";
                ProfileUsername.Text = username;
                
                var profileInfo = !string.IsNullOrWhiteSpace(profile.ProfileInfo) 
                    ? profile.ProfileInfo 
                    : "No profile info";
                ProfileInfo.Text = profileInfo;

                UpdateAvatar(ProfileAvatar, profile.Avatar);
                UpdateAvatar(ChatHeaderAvatar, profile.Avatar);
                
                ChatHeaderName.Text = displayName;

                UpdateChatListAvatar(profile.Id, profile.Avatar);
                CalculateChatStatistics(profile.Id);
            }
            catch
            {
            }
        }

        private void UpdateAvatar(System.Windows.Shapes.Ellipse ellipse, byte[]? avatarData)
        {
            try
            {
                if (ellipse == null)
                {
                    return;
                }
                
                if (avatarData != null && avatarData.Length > 0)
                {
                    try
                    {
                        using (var ms = new System.IO.MemoryStream(avatarData))
                        {
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = ms;
                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            var brush = new System.Windows.Media.ImageBrush(bitmap);
                            brush.Stretch = System.Windows.Media.Stretch.UniformToFill;
                            
                            ellipse.Fill = brush;
                            ellipse.InvalidateVisual();
                        }
                    }
                    catch
                    {
                        SetDefaultAvatarGradient(ellipse);
                    }
                }
                else
                {
                    SetDefaultAvatarGradient(ellipse);
                }
            }
            catch
            {
                SetDefaultAvatarGradient(ellipse);
            }
        }

        private void UpdateChatListAvatar(int userId, byte[]? avatarData)
        {
            try
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    var chat = ChatList.FirstOrDefault(c => c.OtherUserId == userId);
                    if (chat != null && avatarData != null)
                    {
                        // Сохраняем UnreadCount перед обновлением
                        int savedUnreadCount = chat.UnreadCount;
                        
                        chat.Avatar = avatarData;
                        
                        // Восстанавливаем UnreadCount после обновления
                        chat.UnreadCount = savedUnreadCount;
                        
                        // Принудительно обновляем отображение
                        var index = ChatList.IndexOf(chat);
                        if (index >= 0)
                        {
                            ChatList.RemoveAt(index);
                            ChatList.Insert(index, chat);
                        }
                    }
                });
            }
            catch
            {
            }
        }

        private void SetDefaultAvatarGradient(System.Windows.Shapes.Ellipse? ellipse = null)
        {
            try
            {
                var targetEllipse = ellipse ?? ProfileAvatar;
                var accentColor = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["AccentBrush"];
                var color = accentColor.Color;
                
                targetEllipse.Fill = new System.Windows.Media.LinearGradientBrush(
                    color,
                    System.Windows.Media.Color.FromArgb(255, 
                        (byte)Math.Min(255, color.R + 30), 
                        (byte)Math.Min(255, color.G + 30), 
                        (byte)Math.Min(255, color.B + 30)),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(1, 1));
            }
            catch
            {
                var targetEllipse = ellipse ?? ProfileAvatar;
                targetEllipse.Fill = new System.Windows.Media.LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(139, 115, 85),
                    System.Windows.Media.Color.FromRgb(166, 139, 107),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(1, 1));
            }
        }

        private void CalculateChatStatistics(int userId)
        {
            try
            {
                if (_currentRoomId == 0 || ChatMessages == null)
                {
                    ProfileMutualGroups.Text = "0";
                    return;
                }

                int mutualGroups = ChatList.Count(c => 
                    !c.IsGroup && 
                    c.OtherUserId == userId);

                ProfileMutualGroups.Text = mutualGroups.ToString();
                
            }
            catch
            {
                ProfileMutualGroups.Text = "0";
            }
        }

        private void SearchHeaderButton_Click(object sender, RoutedEventArgs e)
        {
        }

        private void CallHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("This feature will be added in the next update.", "Coming Soon", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmojiPopup.IsOpen)
            {
                EmojiPopup.IsOpen = false;
            }
            else
            {
                LoadEmojis();
                if (EmojiButton != null)
                {
                    EmojiPopup.PlacementTarget = EmojiButton;
                    EmojiPopup.Placement = PlacementMode.Top;
                    EmojiPopup.HorizontalOffset = -150;
                    EmojiPopup.VerticalOffset = -10;
                }
                EmojiPopup.IsOpen = true;
            }
        }

        private void LoadEmojis()
        {
            try
            {
                var emojis = new List<string>
                {
                    "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣",
                    "😊", "😇", "🙂", "🙃", "😉", "😌", "😍", "🥰",
                    "😘", "😗", "😙", "😚", "😋", "😛", "😝", "😜",
                    "🤪", "🤨", "🧐", "🤓", "😎", "🤩", "🥳", "😏",
                    "😒", "😞", "😔", "😟", "😕", "🙁", "☹️", "😣",
                    "😖", "😫", "😩", "🥺", "😢", "😭", "😤", "😠",
                    "😡", "🤬", "🤯", "😳", "🥵", "🥶", "😱", "😨",
                    "😰", "😥", "😓", "🤗", "🤔", "🤭", "🤫", "🤥",
                    "😶", "😐", "😑", "😬", "🙄", "😯", "😦", "😧",
                    "😮", "😲", "🥱", "😴", "🤤", "😪", "😵", "🤐",
                    "🥴", "🤢", "🤮", "🤧", "😷", "🤒", "🤕", "🤑",
                    "🤠", "😈", "👿", "👹", "👺", "🤡", "💩", "👻",
                    "💀", "☠️", "👽", "👾", "🤖", "🎃", "😺", "😸",
                    "😹", "😻", "😼", "😽", "🙀", "😿", "😾", "👋",
                    "🤚", "🖐", "✋", "🖖", "👌", "🤏", "✌️", "🤞",
                    "🤟", "🤘", "🤙", "👈", "👉", "👆", "🖕", "👇",
                    "☝️", "👍", "👎", "✊", "👊", "🤛", "🤜", "👏",
                    "🙌", "👐", "🤲", "🤝", "🙏", "✍️", "💪", "🦵",
                    "🦶", "👂", "👃", "🧠", "🦷", "🦴", "👀", "👁️",
                    "👅", "👄", "💋", "💘", "💝", "💖", "💗", "💓",
                    "💞", "💕", "💟", "❣️", "💔", "❤️", "🧡", "💛",
                    "💚", "💙", "💜", "🖤", "🤍", "🤎", "💯", "💢",
                    "💥", "💫", "💦", "💨", "🕳️", "💣", "💬", "👁️‍🗨️",
                    "🗨️", "🗯️", "💭", "💤", "👋", "🤚", "🖐", "✋"
                };

                EmojiList.ItemsSource = emojis;
            }
            catch
            {
            }
        }

        private void EmojiItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string emoji)
            {
                if (MessageInput == null) return;

                if (MessageInput.Text == "Send a message...")
                {
                    MessageInput.Text = "";
                    MessageInput.Foreground = new SolidColorBrush(Colors.Black);
                }

                int caretIndex = MessageInput.CaretIndex;
                MessageInput.Text = MessageInput.Text.Insert(caretIndex, emoji);
                MessageInput.CaretIndex = caretIndex + emoji.Length;
                MessageInput.Focus();

                EmojiPopup.IsOpen = false;
            }
        }

        private void ProcessChatDeleted(ApiResponse response)
        {
            try
            {
                int? deletedChatId = null;
                if (response.Data is JsonElement dataElement)
                {
                    if (dataElement.TryGetProperty("ChatRoomId", out var chatIdElement))
                    {
                        deletedChatId = chatIdElement.GetInt32();
                    }
                }

                if (deletedChatId.HasValue && deletedChatId.Value == _currentRoomId)
                {
                    ShowChatsPage();
                }

                if (deletedChatId.HasValue)
                {
                    var chatToRemove = ChatList.FirstOrDefault(c => c.Id == deletedChatId.Value);
                    if (chatToRemove != null)
                    {
                        ChatList.Remove(chatToRemove);
                    }
                }
                else
                {
                    _ = LoadChatsAsync();
                }
            }
            catch
            {
            }
        }

        private async void DeleteChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoomId == 0)
            {
                MessageBox.Show("No chat selected");
                return;
            }

            var result = MessageBox.Show(
                "Are you sure you want to delete this chat? This action cannot be undone.\nThe chat will be deleted for both users.",
                "Delete Chat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (_network == null || !_network.IsConnected)
                    {
                        MessageBox.Show("No connection to server");
                        return;
                    }

                    var response = await _network.DeleteChatAsync(_currentRoomId);
                    if (response?.Success == true)
                    {
                        var chatToRemove = ChatList.FirstOrDefault(c => c.Id == _currentRoomId);
                        if (chatToRemove != null)
                        {
                            ChatList.Remove(chatToRemove);
                        }

                        ShowChatsPage();
                        
                        await LoadChatsAsync();
                        
                        MessageBox.Show("Chat deleted successfully", "Success", 
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(response?.Message ?? "Failed to delete chat", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting chat: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }



        private System.Threading.Timer? _refreshTimer;
        private readonly object _refreshLock = new object();

        private void RefreshMessagesView()
        {
            lock (_refreshLock)
            {
                _refreshTimer?.Dispose();
                _refreshTimer = new System.Threading.Timer(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        CollectionViewSource.GetDefaultView(ChatMessages)?.Refresh();
                    });
                    _refreshTimer?.Dispose();
                    _refreshTimer = null;
                }, null, 50, Timeout.Infinite);
            }
        }

        private void ProcessMessageUpdated(JsonElement msgData)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var updatedMessage = JsonSerializer.Deserialize<MessageDto>(msgData.GetRawText(), options);
                if (updatedMessage == null) return;

                var existingMessage = ChatMessages.FirstOrDefault(m => m.Id == updatedMessage.Id);
                if (existingMessage != null)
                {
                    existingMessage.Content = updatedMessage.Content;
                    existingMessage.EditedAt = updatedMessage.EditedAt;
                    
                    RefreshMessagesView();
                    
                }
                else
                {
                }
            }
            catch
            {
            }
        }

        private void ProcessMessageDeleted(JsonElement deleteData)
        {
            try
            {
                if (!deleteData.TryGetProperty("MessageId", out var messageIdElement) ||
                    !deleteData.TryGetProperty("ChatRoomId", out var chatRoomIdElement))
                {
                    return;
                }

                var messageId = messageIdElement.GetInt32();
                var chatRoomId = chatRoomIdElement.GetInt32();

                if (chatRoomId == _currentRoomId)
                {
                    var messageToRemove = ChatMessages.FirstOrDefault(m => m.Id == messageId);
                    if (messageToRemove != null)
                    {
                        ChatMessages.Remove(messageToRemove);
                    }
                    else
                    {
                    }
                }

                var chat = ChatList.FirstOrDefault(c => c.Id == chatRoomId);
                if (chat != null)
                {
                    var lastMessage = ChatMessages.Where(m => m.ChatRoomId == chatRoomId)
                        .OrderByDescending(m => m.SentAt)
                        .FirstOrDefault();
                    
                    if (lastMessage != null)
                    {
                        chat.LastMessage = lastMessage.Content;
                        chat.LastMessageTime = lastMessage.SentAt;
                    }
                    else
                    {
                        chat.LastMessage = "No messages";
                        chat.LastMessageTime = DateTime.MinValue;
                    }
                }
            }
            catch
            {
            }
        }

        private async void EditMessageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is MessageDto message)
            {
                if (message.MessageType != Uchat.Shared.Enums.MessageType.Text)
                {
                    MessageBox.Show("Only text messages can be edited", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (message.UserId != _userSession.UserId)
                {
                    MessageBox.Show("You can only edit your own messages", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dialog = new Window
                {
                    Title = "Edit message",
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };

                var textBox = new TextBox
                {
                    Text = message.Content,
                    Margin = new Thickness(10),
                    VerticalAlignment = VerticalAlignment.Top,
                    Height = 100,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                var okButton = new Button
                {
                    Content = "Save",
                    Width = 100,
                    Height = 30,
                    Margin = new Thickness(0, 0, 10, 10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsDefault = true
                };
                okButton.Click += (s, e) =>
                {
                    dialog.DialogResult = true;
                    dialog.Close();
                };

                var cancelButton = new Button
                {
                    Content = "Cancel",
                    Width = 100,
                    Height = 30,
                    Margin = new Thickness(10, 0, 10, 10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsCancel = true
                };
                cancelButton.Click += (s, e) =>
                {
                    dialog.DialogResult = false;
                    dialog.Close();
                };

                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                stackPanel.Children.Add(cancelButton);
                stackPanel.Children.Add(okButton);

                var grid = new Grid();
                grid.Children.Add(textBox);
                grid.Children.Add(stackPanel);
                dialog.Content = grid;

                textBox.Focus();
                textBox.SelectAll();

                bool? result = dialog.ShowDialog();
                if (result == true && !string.IsNullOrWhiteSpace(textBox.Text) && textBox.Text != message.Content)
                {
                    if (_network != null)
                    {
                        var response = await _network.EditMessageAsync(message.Id, textBox.Text);
                        if (!response.Success)
                        {
                            MessageBox.Show($"Error editing message: {response.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
        }

        private async void DeleteMessageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is MessageDto message)
            {
                if (message.UserId != _userSession.UserId)
                {
                    MessageBox.Show("You can only delete your own messages", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    "Are you sure you want to delete this message?",
                    "Confirm deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (_network != null)
                    {
                        var response = await _network.DeleteMessageAsync(message.Id);
                        if (!response.Success)
                        {
                            MessageBox.Show($"Error deleting message: {response.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
        }

    }
}