using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using uchat.Models;
using uchat.Services;
using uchat.Views;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;
using Microsoft.Win32;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient? _network;
        private readonly UserSession _userSession;
        private int _currentRoomId = 0;
        private readonly string _downloadRoot;
        private readonly ConcurrentDictionary<int, Task> _attachmentDownloads = new ConcurrentDictionary<int, Task>();
        private bool _isUploadingFile = false;
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
        };
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v"
        };
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".aac", ".flac", ".ogg", ".m4a"
        };
        private const long InlineDownloadLimitBytes = 20L * 1024L * 1024L;

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
        private readonly Dictionary<Slider, MediaElement> _inlineVideoSliders = new();
        private readonly HashSet<Slider> _activeSliderGestures = new();
        private readonly DispatcherTimer _videoProgressTimer;

        public MainWindow(NetworkClient network, UserSession userSession)
        {
            InitializeComponent();
            _userSession = userSession;
            _network = network;

            _videoProgressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _videoProgressTimer.Tick += VideoProgressTimer_Tick;
            
            UserSession.Current = _userSession;

            _downloadRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UChat", "Downloads");
            try
            {
                Directory.CreateDirectory(_downloadRoot);
            }
            catch
            {
            }

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
                _videoProgressTimer.Stop();
                _inlineVideoSliders.Clear();
                _activeSliderGestures.Clear();

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
                            PrefetchAttachmentIfNeeded(msg);
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
                        PrefetchAttachmentIfNeeded(msgDto);
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

        private void PrefetchAttachmentIfNeeded(MessageDto? message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.FileUrl))
            {
                return;
            }

            bool shouldPrefetch = false;

            if (message.MessageType == MessageType.Image)
            {
                shouldPrefetch = true;
            }
            else if (IsVideoMessage(message))
            {
                bool withinInlineLimit = message.FileSize <= 0 || message.FileSize <= InlineDownloadLimitBytes;
                shouldPrefetch = withinInlineLimit;
            }

            if (shouldPrefetch)
            {
                _ = EnsureInlineAttachmentAsync(message);
            }

            bool IsVideoMessage(MessageDto msg)
            {
                if (msg.MessageType == MessageType.Video)
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(msg.FileName))
                {
                    var extension = Path.GetExtension(msg.FileName);
                    if (!string.IsNullOrWhiteSpace(extension) && VideoExtensions.Contains(extension))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private Task EnsureInlineAttachmentAsync(MessageDto message, bool force = false, bool openAfterDownload = false)
        {
            if (_network == null || !_network.IsConnected)
            {
                return Task.CompletedTask;
            }

            if (!force && !string.IsNullOrWhiteSpace(message.LocalFilePath) && File.Exists(message.LocalFilePath))
            {
                if (openAfterDownload)
                {
                    OpenFileWithShell(message.LocalFilePath);
                }
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(message.FileUrl))
            {
                return Task.CompletedTask;
            }

            return _attachmentDownloads.GetOrAdd(message.Id, _ => DownloadInlineAsync());

            async Task DownloadInlineAsync()
            {
                try
                {
                    var localPath = await FetchAndSaveAttachmentAsync(message, force);
                    if (!string.IsNullOrEmpty(localPath) && openAfterDownload)
                    {
                        OpenFileWithShell(localPath);
                    }
                }
                catch (Exception ex)
                {
                    if (force)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Не удалось загрузить вложение: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
                finally
                {
                    _attachmentDownloads.TryRemove(message.Id, out _);
                }
            }
        }

        private async Task<string?> FetchAndSaveAttachmentAsync(MessageDto message, bool force)
        {
            if (_network == null || !_network.IsConnected)
            {
                throw new InvalidOperationException("Нет подключения к серверу");
            }

            var safeFolder = Path.Combine(_downloadRoot, message.ChatRoomId.ToString());
            Directory.CreateDirectory(safeFolder);

            var targetFileName = EnsureSafeFileName(!string.IsNullOrWhiteSpace(message.FileName)
                ? message.FileName
                : message.FileUrl);
            var localPath = Path.Combine(safeFolder, targetFileName);

            if (!force && File.Exists(localPath))
            {
                message.LocalFilePath = localPath;
                RefreshMessagesViewByDispatcher();
                return localPath;
            }

            var downloadResult = await _network.DownloadFileInlineAsync(message.FileUrl);
            if (!downloadResult.Success || downloadResult.Data == null)
            {
                throw new InvalidOperationException(downloadResult.Message ?? "Не удалось загрузить файл");
            }

            await File.WriteAllBytesAsync(localPath, downloadResult.Data);
            message.LocalFilePath = localPath;
            RefreshMessagesViewByDispatcher();

            return localPath;
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

        private async void AttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoomId == 0)
            {
                MessageBox.Show("Сначала выберите чат", "Нет активного чата", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isUploadingFile)
            {
                MessageBox.Show("Дождитесь завершения текущей загрузки", "Файл отправляется", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Выберите файл для отправки",
                Filter = "Все поддерживаемые|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.mov;*.avi;*.mkv;*.webm;*.mp3;*.wav;*.flac;*.ogg;*.zip;*.rar;*.7z;*.pdf;*.doc;*.docx;*.xls;*.xlsx|Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Все файлы|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                await UploadAttachmentAsync(dialog.FileName);
            }
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

        private async Task UploadAttachmentAsync(string filePath)
        {
            if (_network == null || !_network.IsConnected)
            {
                MessageBox.Show("Нет подключения к серверу", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(filePath))
            {
                MessageBox.Show("Файл не найден", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_currentRoomId == 0)
            {
                MessageBox.Show("Выберите чат перед отправкой файла", "Нет активного чата", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var messageType = ResolveMessageType(filePath);
            var fileName = Path.GetFileName(filePath);

            _isUploadingFile = true;
            UpdateUploadStatus($"Отправляем {fileName}", true);

            try
            {
                var response = await _network.UploadFileAsync(_currentRoomId, filePath, messageType);
                if (!response.Success)
                {
                    MessageBox.Show(response.Message ?? "Не удалось загрузить файл", "Ошибка загрузки", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки файла: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUploadingFile = false;
                UpdateUploadStatus(string.Empty, false);
            }
        }

        private void UpdateUploadStatus(string message, bool isVisible)
        {
            if (UploadStatusPanel == null || UploadStatusText == null)
            {
                return;
            }

            UploadStatusPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            UploadStatusText.Text = isVisible ? message : string.Empty;
            if (AttachmentButton != null)
            {
                AttachmentButton.IsEnabled = !isVisible;
            }
        }

        private MessageType ResolveMessageType(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return MessageType.File;
            }

            if (ImageExtensions.Contains(extension))
            {
                return MessageType.Image;
            }

            if (AudioExtensions.Contains(extension))
            {
                return MessageType.Audio;
            }

            if (VideoExtensions.Contains(extension))
            {
                return MessageType.Video;
            }

            return MessageType.File;
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

        private async void DownloadAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is MessageDto message)
            {
                await DownloadAttachmentWithDialogAsync(message);
            }
        }

        private async void OpenAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is MessageDto message)
            {
                if (string.IsNullOrWhiteSpace(message.LocalFilePath) || !File.Exists(message.LocalFilePath))
                {
                    if (message.MessageType == MessageType.Image)
                    {
                        await EnsureInlineAttachmentAsync(message, force: true, openAfterDownload: true);
                        return;
                    }

                    await DownloadAttachmentWithDialogAsync(message);
                }
                else
                {
                    OpenFileWithShell(message.LocalFilePath);
                }
            }
        }

        private async void ImageAttachment_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image image && image.DataContext is MessageDto message)
            {
                await EnsureInlineAttachmentAsync(message, force: true, openAfterDownload: true);
            }
        }

        private void InlineVideoPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (!TryGetMediaElementFromButton(button, out var mediaElement) || mediaElement == null)
            {
                return;
            }

            if (!EnsureMediaSource(mediaElement, button.DataContext))
            {
                MessageBox.Show("Видео ещё не загружено", "Воспроизведение", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                mediaElement.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось начать воспроизведение: {ex.Message}", "Видео", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void InlineVideoPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (!TryGetMediaElementFromButton(button, out var mediaElement) || mediaElement == null)
            {
                return;
            }

            if (!EnsureMediaSource(mediaElement, button.DataContext))
            {
                return;
            }

            mediaElement.Pause();
        }

        private void InlineVideoStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (!TryGetMediaElementFromButton(button, out var mediaElement) || mediaElement == null)
            {
                return;
            }

            mediaElement.Stop();
        }

        private void InlineVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is MediaElement element)
            {
                element.Stop();
            }
        }

        private void InlineVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MediaElement mediaElement)
            {
                return;
            }

            if (!mediaElement.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            var duration = mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration))
            {
                return;
            }

            foreach (var pair in _inlineVideoSliders.ToArray())
            {
                if (pair.Value == mediaElement)
                {
                    pair.Key.Maximum = duration;
                }
            }
        }

        private void InlineVideoSlider_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Slider slider)
            {
                return;
            }

            if (slider.Tag is not MediaElement mediaElement)
            {
                return;
            }

            _inlineVideoSliders[slider] = mediaElement;
            EnsureVideoTimerState();
        }

        private void InlineVideoSlider_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Slider slider)
            {
                return;
            }

            _inlineVideoSliders.Remove(slider);
            _activeSliderGestures.Remove(slider);
            EnsureVideoTimerState();
        }

        private void InlineVideoSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider slider)
            {
                _activeSliderGestures.Add(slider);
            }
        }

        private void InlineVideoSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider slider)
            {
                if (_activeSliderGestures.Remove(slider))
                {
                    ApplySliderPosition(slider);
                }
            }
        }

        private void InlineVideoSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (sender is Slider slider && _activeSliderGestures.Remove(slider))
            {
                ApplySliderPosition(slider);
            }
        }

        private bool TryGetMediaElementFromButton(Button button, out MediaElement? mediaElement)
        {
            if (button.Tag is MediaElement directElement)
            {
                mediaElement = directElement;
                return true;
            }

            mediaElement = null;
            return false;
        }

        private bool EnsureMediaSource(MediaElement element, object? context)
        {
            if (element.Source != null)
            {
                return true;
            }

            if (context is MessageDto message)
            {
                var path = message.LocalFilePath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    try
                    {
                        element.Source = new Uri(Path.GetFullPath(path));
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        private void ApplySliderPosition(Slider slider)
        {
            if (!_inlineVideoSliders.TryGetValue(slider, out var mediaElement))
            {
                return;
            }

            if (!mediaElement.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            var targetSeconds = slider.Value;
            var maxSeconds = mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            if (double.IsNaN(targetSeconds) || double.IsInfinity(targetSeconds) || maxSeconds <= 0)
            {
                return;
            }

            targetSeconds = Math.Max(0, Math.Min(targetSeconds, maxSeconds));
            mediaElement.Position = TimeSpan.FromSeconds(targetSeconds);
        }

        private void VideoProgressTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var pair in _inlineVideoSliders.ToArray())
            {
                var slider = pair.Key;
                var media = pair.Value;

                if (slider == null || media == null)
                {
                    continue;
                }

                if (!media.NaturalDuration.HasTimeSpan)
                {
                    continue;
                }

                var duration = media.NaturalDuration.TimeSpan.TotalSeconds;
                if (duration <= 0)
                {
                    continue;
                }

                if (!double.IsNaN(duration) && !double.IsInfinity(duration))
                {
                    slider.Maximum = duration;
                }

                if (_activeSliderGestures.Contains(slider))
                {
                    continue;
                }

                var currentSeconds = media.Position.TotalSeconds;
                if (!double.IsNaN(currentSeconds) && !double.IsInfinity(currentSeconds))
                {
                    slider.Value = Math.Max(0, Math.Min(currentSeconds, duration));
                }
            }
        }

        private void EnsureVideoTimerState()
        {
            if (_inlineVideoSliders.Count > 0)
            {
                if (!_videoProgressTimer.IsEnabled)
                {
                    _videoProgressTimer.Start();
                }
            }
            else if (_videoProgressTimer.IsEnabled)
            {
                _videoProgressTimer.Stop();
            }
        }

        private async Task DownloadAttachmentWithDialogAsync(MessageDto message)
        {
            if (_network == null || !_network.IsConnected)
            {
                MessageBox.Show("Нет подключения к серверу", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(message.FileUrl))
            {
                MessageBox.Show("Вложение недоступно для скачивания", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                FileName = EnsureSafeFileName(!string.IsNullOrWhiteSpace(message.FileName) ? message.FileName : message.FileUrl),
                Filter = "Все файлы (*.*)|*.*",
                Title = "Сохранить вложение"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var destination = dialog.FileName;

                if (message.FileSize > InlineDownloadLimitBytes)
                {
                    await DownloadLargeFileAsync(message, destination);
                }
                else
                {
                    var inlineResult = await _network.DownloadFileInlineAsync(message.FileUrl);
                    if (inlineResult.Success && inlineResult.Data != null)
                    {
                        await File.WriteAllBytesAsync(destination, inlineResult.Data);
                    }
                    else
                    {
                        await DownloadLargeFileAsync(message, destination, inlineResult.Message);
                    }
                }

                message.LocalFilePath = destination;
                RefreshMessagesViewByDispatcher();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка скачивания файла: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadLargeFileAsync(MessageDto message, string destinationPath, string? fallbackReason = null)
        {
            var downloadResult = await _network!.DownloadFileAsync(message.FileUrl, destinationPath);
            if (!downloadResult.Success)
            {
                throw new InvalidOperationException(downloadResult.Message ?? fallbackReason ?? "Не удалось скачать файл");
            }
        }

        private void OpenFileWithShell(string localPath)
        {
            if (!File.Exists(localPath))
            {
                MessageBox.Show("Файл не найден на диске", "Открыть файл", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(localPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть файл: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string EnsureSafeFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return $"file_{Guid.NewGuid():N}";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var buffer = new char[fileName.Length];
            for (int i = 0; i < fileName.Length; i++)
            {
                var ch = fileName[i];
                buffer[i] = invalidChars.Contains(ch) ? '_' : ch;
            }

            return new string(buffer);
        }

        private void RefreshMessagesViewByDispatcher()
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshMessagesView();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(RefreshMessagesView);
            }
        }

    }
}