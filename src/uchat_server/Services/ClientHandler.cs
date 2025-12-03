using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Uchat.Shared.DTOs;

namespace uchat_server.Services
{
    public class ClientHandler
    {
        private readonly TcpClient _client;
        private readonly AuthService _authService;
        private readonly ChatService _chatService;
        private readonly ConnectionManager _connectionManager;
        private readonly ILogger<ClientHandler> _logger;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private int _currentUserId = 0;
        private string _currentUsername = "";
        private int _currentChatRoomId = 0;

        public int CurrentUserId => _currentUserId;
        public string CurrentUsername => _currentUsername;
        public int CurrentChatRoomId => _currentChatRoomId;

        public ClientHandler(TcpClient client, AuthService authService, ChatService chatService,
                           ConnectionManager connectionManager, ILogger<ClientHandler> logger)
        {
            _client = client;
            _authService = authService;
            _chatService = chatService;
            _connectionManager = connectionManager;
            _logger = logger;
        }

        public async Task StartAsync()
        {
            try
            {
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

                await SendWelcomeMessage();

                string? message;
                while ((message = await _reader.ReadLineAsync()) != null)
                {
                    if (message.StartsWith("/"))
                    {
                        await ProcessCommandAsync(message);
                    }
                    else
                    {
                        await HandleTextMessageAsync(message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client handling error");
            }
            finally
            {
                if (_currentUserId > 0)
                {
                    _connectionManager.RemoveConnection(_currentUserId, this);
                    if (_currentChatRoomId > 0)
                    {
                        _connectionManager.LeaveRoom(_currentUserId, _currentChatRoomId, this);
                    }
                }
                _client.Close();
            }
        }

        private async Task ProcessCommandAsync(string message)
        {
            string[] parts = message.Split(' ');
            string command = parts[0].ToLower();

            switch (command)
            {
                case "/login":
                    if (parts.Length == 3)
                    {
                        await LoginUserAsync(parts[1], parts[2]);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /login <username> <password>");
                    }
                    break;

                case "/register":
                    if (parts.Length == 3)
                    {
                        await RegisterUserAsync(parts[1], parts[2], "", null);
                    }
                    else if (parts.Length == 4)
                    {
                        await RegisterUserAsync(parts[1], parts[2], parts[3], null);
                    }
                    else if (parts.Length == 5)
                    {
                        await RegisterUserAsync(parts[1], parts[2], parts[3], parts[4]);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /register <username> <password> [firstName] [lastName]");
                    }
                    break;

                case "/chat":
                    if (parts.Length == 2)
                    {
                        await StartPrivateChatAsync(parts[1]);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /chat <username>");
                    }
                    break;

                case "/join":
                    if (parts.Length == 2 && int.TryParse(parts[1], out int roomId))
                    {
                        await JoinChatRoomAsync(roomId);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /join <roomId>");
                    }
                    break;

                case "/getchats":
                    await GetUserChatsAsync();
                    break;

                default:
                    await SendResponseAsync(false, $"Unknown command: {command}");
                    break;
            }
        }

        private async Task GetUserChatsAsync()
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var userChats = await _chatService.GetUserChatRoomsAsync(_currentUserId);
                var chatInfos = new List<ChatInfoDto>();

                foreach (var chat in userChats)
                {
                    var otherMember = chat.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                    if (otherMember != null)
                    {
                        var otherUser = await _chatService.GetUserByIdAsync(otherMember.UserId);
                        var lastMessage = await _chatService.GetLastMessageAsync(chat.Id);

                        chatInfos.Add(new ChatInfoDto
                        {
                            Id = chat.Id,
                            Name = chat.Name,
                            DisplayName = otherUser?.Username ?? "Unknown",
                            IsGroup = chat.IsGroup,
                            Description = lastMessage?.Content ?? "��� ���������",
                            OtherUserId = otherMember.UserId,
                            OtherUsername = otherUser?.Username ?? "Unknown",
                            CreatedAt = chat.CreatedAt,
                            UnreadCount = 0,
                            LastMessage = lastMessage?.Content,
                            LastMessageTime = lastMessage?.SentAt
                        });
                    }
                }

                await SendResponseAsync(true, "User chats", chatInfos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetUserChatsAsync");
                await SendResponseAsync(false, $"Error getting chats: {ex.Message}");
            }
        }

        private async Task HandleTextMessageAsync(string messageContent)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (_currentChatRoomId == 0)
                {
                    await SendResponseAsync(false, "You are not in a chat");
                    return;
                }

                var messageDto = await _chatService.SaveMessageAsync(_currentUserId, _currentChatRoomId, messageContent);
                await BroadcastToChatRoomAsync(messageDto);
                await SendResponseAsync(true, "Message sent", messageDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message");
                await SendResponseAsync(false, $"Error sending message: {ex.Message}");
            }
        }

        private async Task BroadcastToChatRoomAsync(MessageDto messageDto)
        {
            try
            {
                var roomConnections = _connectionManager.GetRoomConnections(_currentChatRoomId);
                foreach (var connection in roomConnections)
                {
                    try
                    {
                        await connection.SendMessageToClientAsync(messageDto);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send to user");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BroadcastToChatRoomAsync");
            }
        }

        public async Task SendMessageToClientAsync(MessageDto messageDto)
        {
            try
            {
                var response = new ApiResponse
                {
                    Success = true,
                    Message = "New message",
                    Data = messageDto
                };

                string json = JsonSerializer.Serialize(response);
                if (_writer != null)
                {
                    await _writer.WriteLineAsync(json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to client");
            }
        }

        private async Task StartPrivateChatAsync(string targetUsername)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var targetUser = await _chatService.GetUserByUsernameAsync(targetUsername);

                if (targetUser == null)
                {
                    await SendResponseAsync(false, $"User '{targetUsername}' not found");
                    return;
                }

                if (targetUser.Id == _currentUserId)
                {
                    await SendResponseAsync(false, "You cannot start a chat with yourself");
                    return;
                }

                var chatRoom = await _chatService.GetOrCreatePrivateChatAsync(_currentUserId, targetUser.Id);
                _currentChatRoomId = chatRoom.Id;

                _connectionManager.JoinRoom(_currentUserId, _currentChatRoomId, this);

                var targetConnections = _connectionManager.GetUserConnection(targetUser.Id);
                if (targetConnections != null)
                {
                    _connectionManager.JoinRoom(targetUser.Id, _currentChatRoomId, targetConnections);
                    await targetConnections.SendResponseAsync(true, $"{_currentUsername} started a chat with you", new
                    {
                        RoomId = chatRoom.Id,
                        OtherUser = _currentUsername
                    });
                }

                var history = await _chatService.GetRoomMessagesAsync(chatRoom.Id);

                await SendResponseAsync(true, $"Chat started with {targetUsername}", new
                {
                    RoomId = chatRoom.Id,
                    TargetUser = targetUser.Username,
                    OtherUserId = targetUser.Id,
                    History = history
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in StartPrivateChatAsync");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task JoinChatRoomAsync(int roomId)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var chatRoom = await _chatService.GetChatRoomAsync(roomId);
                if (chatRoom == null)
                {
                    await SendResponseAsync(false, $"Chat room {roomId} not found");
                    return;
                }

                var isMember = await _chatService.IsUserInRoomAsync(_currentUserId, roomId);
                if (!isMember)
                {
                    await SendResponseAsync(false, "You are not a member of this chat");
                    return;
                }

                _currentChatRoomId = roomId;
                _connectionManager.JoinRoom(_currentUserId, _currentChatRoomId, this);

                var otherMember = chatRoom.Members.First(m => m.UserId != _currentUserId);
                var otherUser = await _chatService.GetUserByIdAsync(otherMember.UserId);

                var history = await _chatService.GetRoomMessagesAsync(roomId);

                await SendResponseAsync(true, $"Joined chat room", new
                {
                    RoomId = chatRoom.Id,
                    TargetUser = otherUser?.Username ?? "Unknown",
                    OtherUserId = otherMember.UserId,
                    History = history
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in JoinChatRoomAsync");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task LoginUserAsync(string username, string password)
        {
            try
            {
                var result = await _authService.LoginAsync(username, password);
                if (result.Success)
                {
                    try
                    {
                        if (result.Data is JsonElement jsonElement)
                        {
                            var authData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonElement.GetRawText());
                            if (authData != null)
                            {
                                if (authData.TryGetValue("UserId", out var userIdElement))
                                {
                                    _currentUserId = userIdElement.GetInt32();
                                }
                                if (authData.TryGetValue("Username", out var usernameElement))
                                {
                                    _currentUsername = usernameElement.GetString() ?? "";
                                }
                            }
                        }
                        else if (result.Data != null)
                        {
                            var type = result.Data.GetType();
                            var userIdProp = type.GetProperty("UserId");
                            var usernameProp = type.GetProperty("Username");

                            if (userIdProp != null)
                            {
                                var userIdValue = userIdProp.GetValue(result.Data);
                                if (userIdValue != null)
                                {
                                    _currentUserId = Convert.ToInt32(userIdValue);
                                }
                            }

                            if (usernameProp != null)
                            {
                                var usernameValue = usernameProp.GetValue(result.Data);
                                _currentUsername = (usernameValue?.ToString()) ?? "";
                            }
                        }

                        if (_currentUserId > 0)
                        {
                            _connectionManager.AddConnection(_currentUserId, this);

                            var userChats = await _chatService.GetUserChatRoomsAsync(_currentUserId);
                            var chatInfos = new List<ChatInfoDto>();

                            foreach (var chat in userChats)
                            {
                                var otherMember = chat.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                                if (otherMember != null)
                                {
                                    var otherUser = await _chatService.GetUserByIdAsync(otherMember.UserId);
                                    var lastMessage = await _chatService.GetLastMessageAsync(chat.Id);

                                    chatInfos.Add(new ChatInfoDto
                                    {
                                        Id = chat.Id,
                                        Name = chat.Name,
                                        DisplayName = otherUser?.Username ?? "Unknown",
                                        IsGroup = chat.IsGroup,
                                        Description = lastMessage?.Content ?? "��� ���������",
                                        OtherUserId = otherMember.UserId,
                                        OtherUsername = otherUser?.Username ?? "Unknown",
                                        CreatedAt = chat.CreatedAt,
                                        UnreadCount = 0,
                                        LastMessage = lastMessage?.Content,
                                        LastMessageTime = lastMessage?.SentAt
                                    });
                                }
                            }

                            var responseData = new
                            {
                                UserId = _currentUserId,
                                Username = _currentUsername,
                                Chats = chatInfos
                            };

                            await SendResponseAsync(true, "Login successful", responseData);
                        }
                        else
                        {
                            await SendResponseAsync(false, "Login failed: Could not parse user data");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error parsing login response");
                        await SendResponseAsync(false, "Login failed: Invalid response format");
                    }
                }
                else
                {
                    await SendResponseAsync(false, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in LoginUserAsync");
                await SendResponseAsync(false, $"Login error: {ex.Message}");
            }
        }

        private async Task RegisterUserAsync(string username, string password, string firstName, string? lastName)
        {
            var result = await _authService.RegisterAsync(username, password, firstName, lastName);
            if (result.Success)
            {
                await SendResponseAsync(true, "Registration successful", result.Data);
            }
            else
            {
                await SendResponseAsync(false, result.Message);
            }
        }

        private async Task SendWelcomeMessage()
        {
            await SendResponseAsync(true, "Welcome to Uchat! Use /help for commands");
        }

        private async Task SendResponseAsync(bool success, string message, object? data = null)
        {
            try
            {
                var response = new ApiResponse
                {
                    Success = success,
                    Message = message,
                    Data = data
                };

                string json = JsonSerializer.Serialize(response);
                if (_writer != null)
                {
                    await _writer.WriteLineAsync(json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending response");
            }
        }

        public async Task SendResponseAsync(ApiResponse response)
        {
            try
            {
                string json = JsonSerializer.Serialize(response);
                if (_writer != null)
                {
                    await _writer.WriteLineAsync(json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending response");
            }
        }

        public async Task SendDtoAsync(object dto)
        {
            var notificationResponse = new ApiResponse
            {
                Success = true,
                Message = "Notification sent.",
                Data = dto
            };

            await SendResponseAsync(notificationResponse);
        }
    }
}