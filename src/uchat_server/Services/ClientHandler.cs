using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;
using Uchat.Shared.Models;

namespace uchat_server.Services
{
    public class ClientHandler
    {
        private readonly TcpClient _client;
        private readonly AuthService _authService;
        private readonly ChatService _chatService;
        private readonly ConnectionManager _connectionManager;
        private readonly FileStorageService _fileStorageService;
        private readonly ILogger<ClientHandler> _logger;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private int _currentUserId = 0;
        private string _currentUsername = "";
        private int _currentChatRoomId = 0;
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
        };

        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".aac", ".flac", ".ogg", ".m4a"
        };

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v"
        };

        public int CurrentUserId => _currentUserId;
        public string CurrentUsername => _currentUsername;
        public int CurrentChatRoomId => _currentChatRoomId;

        public ClientHandler(TcpClient client, AuthService authService, ChatService chatService,
                           ConnectionManager connectionManager, FileStorageService fileStorageService,
                           ILogger<ClientHandler> logger)
        {
            _client = client;
            _authService = authService;
            _chatService = chatService;
            _connectionManager = connectionManager;
            _fileStorageService = fileStorageService;
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
            catch (System.IO.IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx && 
                                                   (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                                    socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
            {
                // Нормальное закрытие соединения клиентом
                _logger.LogInformation("Client disconnected normally: {Message}", ex.Message);
            }
            catch (System.IO.IOException ex) when (ex.Message.Contains("разорвала установленное подключение") || 
                                                   ex.Message.Contains("broken pipe") ||
                                                   ex.Message.Contains("connection reset"))
            {
                // Нормальное закрытие соединения клиентом (альтернативная проверка для русской локали)
                _logger.LogInformation("Client disconnected: connection closed by client");
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                                               ex.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown ||
                                                               ex.SocketErrorCode == System.Net.Sockets.SocketError.Interrupted)
            {
                // Нормальное закрытие соединения клиентом
                _logger.LogInformation("Client disconnected normally: {SocketError}", ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                // Проверяем, не является ли это ошибкой разрыва соединения
                if (ex.Message.Contains("разорвала установленное подключение") || 
                    ex.Message.Contains("broken pipe") ||
                    ex.Message.Contains("connection reset") ||
                    (ex.InnerException is System.Net.Sockets.SocketException se && 
                     (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                      se.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown)))
                {
                    _logger.LogInformation("Client disconnected: connection closed by client");
                }
                else
                {
                    _logger.LogError(ex, "Client handling error");
                }
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
                try
                {
                    _client?.Close();
                }
                catch { }
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
                        await RegisterUserAsync(parts[1], parts[2], parts[1]); // Используем username как displayName по умолчанию
                    }
                    else if (parts.Length == 4)
                    {
                        await RegisterUserAsync(parts[1], parts[2], parts[3]);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /register <username> <password> [displayName]");
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

                case "/updateprofile":
                    await HandleUpdateProfileAsync(parts);
                    break;

                case "/deleteaccount":
                    await HandleDeleteAccountAsync();
                    break;

                case "/getprofile":
                    await HandleGetProfileAsync(parts);
                    break;

                case "/uploadavatar":
                    await HandleUploadAvatarAsync(parts);
                    break;

                case "/deletechat":
                    if (parts.Length == 2 && int.TryParse(parts[1], out int chatIdToDelete))
                    {
                        await HandleDeleteChatAsync(chatIdToDelete);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /deletechat <chatId>");
                    }
                    break;

                case "/upload_file":
                    await HandleUploadFileAsync(parts);
                    break;

                case "/download":
                    if (parts.Length == 2)
                    {
                        await HandleDownloadFileAsync(parts[1]);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /download <fileName>");
                    }
                    break;

                case "/download_inline":
                    await HandleDownloadInlineAsync(parts);
                    break;

                case "/edit_message":
                    await HandleEditMessageAsync(parts);
                    break;

                case "/delete_message":
                    await HandleDeleteMessageAsync(parts);
                    break;

                case "/creategroup":
                    if (parts.Length >= 2)
                    {
                        string groupName = string.Join(" ", parts.Skip(1));
                        await HandleCreateGroupAsync(groupName);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /creategroup <groupName>");
                    }
                    break;

                case "/leavegroup":
                    if (parts.Length == 2 && int.TryParse(parts[1], out int groupIdToLeave))
                    {
                        await HandleLeaveGroupAsync(groupIdToLeave);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /leavegroup <groupId>");
                    }
                    break;

                case "/groupinfo":
                    if (parts.Length == 2 && int.TryParse(parts[1], out int groupId))
                    {
                        await HandleGetGroupInfoAsync(groupId);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /groupinfo <groupId>");
                    }
                    break;

                case "/updategroup":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int groupIdToUpdate))
                    {
                        await HandleUpdateGroupAsync(groupIdToUpdate, parts.Skip(2).ToArray());
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /updategroup <groupId> [name:<name>] [desc:<description>]");
                    }
                    break;

                case "/addmember":
                    if (parts.Length == 3 && int.TryParse(parts[1], out int groupIdToAdd))
                    {
                        string username = parts[2];
                        await HandleAddMemberAsync(groupIdToAdd, username);
                    }
                    else
                    {
                        await SendResponseAsync(false, "Usage: /addmember <groupId> <username>");
                    }
                    break;

                default:
                    await SendResponseAsync(false, $"Unknown command: {command}");
                    break;
            }
        }

        private async Task HandleUpdateProfileAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 2)
                {
                    await SendResponseAsync(false, "Invalid request format");
                    return;
                }

                // Объединяем все части после команды обратно в JSON
                var json = string.Join(" ", parts.Skip(1));
                _logger.LogInformation("Received update profile JSON: {Json}", json);
                
                UpdateProfileRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<UpdateProfileRequest>(json);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize UpdateProfileRequest from JSON: {Json}", json);
                    await SendResponseAsync(false, "Invalid JSON format");
                    return;
                }

                if (request == null)
                {
                    _logger.LogWarning("UpdateProfileRequest is null after deserialization");
                    await SendResponseAsync(false, "Invalid request data");
                    return;
                }

                _logger.LogInformation("UpdateProfileRequest deserialized: Username={Username}, DisplayName={DisplayName}, ProfileInfo={ProfileInfo}, Theme={Theme}, Avatar={AvatarLength}",
                    request.Username, request.DisplayName, request.ProfileInfo, request.Theme, request.Avatar?.Length ?? 0);

                var response = await _authService.UpdateUserProfileAsync(_currentUserId, request);
                await SendResponseAsync(response);

                if (response.Success)
                {
                    // Используем GetData<T>() для получения UserProfileDto
                    var userProfileDto = response.GetData<UserProfileDto>();
                    
                    if (userProfileDto == null && response.Data is JsonElement jsonElement)
                    {
                        // Fallback на прямую десериализацию
                        userProfileDto = JsonSerializer.Deserialize<UserProfileDto>(jsonElement.GetRawText());
                    }
                    
                    if (userProfileDto != null)
                    {
                        await BroadcastProfileUpdate(_currentUserId, userProfileDto);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update profile");
                await SendResponseAsync(false, $"Internal server error: {ex.Message}");
            }
        }

        private async Task HandleDeleteAccountAsync()
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                int userIdToDelete = _currentUserId;
                
                var response = await _authService.DeleteUserAccountAsync(userIdToDelete);
                
                // Отправляем ответ и явно сбрасываем буфер, чтобы убедиться, что ответ отправлен
                await SendResponseAsync(response);
                if (_writer != null)
                {
                    await _writer.FlushAsync();
                }

                if (response.Success)
                {
                    // Сохраняем userId и chatRoomId перед сбросом
                    int userId = _currentUserId;
                    int chatRoomId = _currentChatRoomId;
                    
                    // Сбрасываем текущего пользователя после сохранения значений
                    _currentUserId = 0;
                    _currentUsername = "";
                    _currentChatRoomId = 0;
                    
                    // Даем клиенту время получить ответ перед закрытием соединения
                    await Task.Delay(200);
                    
                    // Удаляем соединение с сохраненным userId
                    _connectionManager.RemoveConnection(userId, this);
                    if (chatRoomId > 0)
                    {
                        _connectionManager.LeaveRoom(userId, chatRoomId, this);
                    }
                }
                else
                {
                    // Сбрасываем только при неудаче, если нужно
                    // (обычно не нужно, но на всякий случай)
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling delete account");
                // Сбрасываем пользователя даже при ошибке
                _currentUserId = 0;
                _currentUsername = "";
                _currentChatRoomId = 0;
                await SendResponseAsync(false, "Internal server error");
            }
        }

        private async Task HandleDeleteChatAsync(int chatRoomId)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                // Получаем информацию о чате до удаления, чтобы узнать второго пользователя
                var chatRoom = await _chatService.GetChatRoomAsync(chatRoomId);
                int? otherUserId = null;
                
                if (chatRoom != null && !chatRoom.IsGroup)
                {
                    var otherMember = chatRoom.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                    if (otherMember != null)
                    {
                        otherUserId = otherMember.UserId;
                    }
                }

                var success = await _chatService.DeleteChatAsync(chatRoomId, _currentUserId);
                if (success)
                {
                    await SendResponseAsync(true, "Chat deleted successfully", new { ChatRoomId = chatRoomId });
                    
                    // Отправляем уведомление второму пользователю, если он онлайн
                    if (otherUserId.HasValue)
                    {
                        var otherUserHandler = _connectionManager.GetUserConnection(otherUserId.Value);
                        if (otherUserHandler != null)
                        {
                            await otherUserHandler.SendResponseAsync(true, "Chat deleted", new { ChatRoomId = chatRoomId });
                        }
                    }
                }
                else
                {
                    await SendResponseAsync(false, "Failed to delete chat");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting chat");
                await SendResponseAsync(false, $"Error deleting chat: {ex.Message}");
            }
        }

        private async Task HandleGetProfileAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 2)
                {
                    await SendResponseAsync(false, "User ID required");
                    return;
                }

                if (!int.TryParse(parts[1], out int targetUserId))
                {
                    await SendResponseAsync(false, "Invalid user ID");
                    return;
                }

                var response = await _authService.GetUserProfileAsync(targetUserId);
                await SendResponseAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling get profile");
                await SendResponseAsync(false, "Internal server error");
            }
        }

        private async Task HandleUploadAvatarAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 2)
                {
                    await SendResponseAsync(false, "Avatar data required");
                    return;
                }

                var base64Data = string.Join(" ", parts.Skip(1));
                var avatarData = Convert.FromBase64String(base64Data);

                var updateRequest = new UpdateProfileRequest
                {
                    DisplayName = _currentUsername,
                    ProfileInfo = "",
                    Theme = "Latte",
                    Avatar = avatarData
                };

                var response = await _authService.UpdateUserProfileAsync(_currentUserId, updateRequest);
                await SendResponseAsync(response);

                if (response.Success)
                {
                    // Используем GetData<T>() для получения UserProfileDto
                    var userProfileDto = response.GetData<UserProfileDto>();
                    
                    if (userProfileDto == null && response.Data is JsonElement jsonElement)
                    {
                        // Fallback на прямую десериализацию
                        userProfileDto = JsonSerializer.Deserialize<UserProfileDto>(jsonElement.GetRawText());
                    }
                    
                    if (userProfileDto != null)
                    {
                        await BroadcastProfileUpdate(_currentUserId, userProfileDto);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling upload avatar");
                await SendResponseAsync(false, "Internal server error");
            }
        }

        private async Task BroadcastProfileUpdate(int userId, UserProfileDto? profileDto)
        {
            try
            {
                if (profileDto == null) return;

                var updateMessage = new ApiResponse
                {
                    Success = true,
                    Message = "Profile updated",
                    Data = profileDto
                };

                var allConnections = _connectionManager.GetAllConnections();
                foreach (var connection in allConnections)
                {
                    try
                    {
                        // ИСПРАВЛЕНА ОШИБКА: используем публичный метод вместо приватного Writer
                        await connection.SendResponseAsync(updateMessage);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error broadcasting profile update to client");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting profile update");
            }
        }

        private void RemoveClientConnection(int? userId = null, int? chatRoomId = null)
        {
            // Используем переданные параметры или текущие значения
            int actualUserId = userId ?? _currentUserId;
            int actualChatRoomId = chatRoomId ?? _currentChatRoomId;
            
            if (actualUserId > 0)
            {
                _connectionManager.RemoveConnection(actualUserId, this);
                if (actualChatRoomId > 0)
                {
                    _connectionManager.LeaveRoom(actualUserId, actualChatRoomId, this);
                }
            }
        }


        private async Task GetUserChatsAsync()
        {
            try
            {
                _logger.LogInformation("GetUserChatsAsync called: _currentUserId={UserId}", _currentUserId);
                if (_currentUserId == 0)
                {
                    _logger.LogWarning("GetUserChatsAsync: _currentUserId is 0, user not logged in");
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var userChats = await _chatService.GetUserChatRoomsAsync(_currentUserId);
                var chatInfos = new List<ChatInfoDto>();

                foreach (var chat in userChats)
                {
                    var lastMessage = await _chatService.GetLastMessageAsync(chat.Id);

                    if (chat.IsGroup)
                    {
                        // Для групповых чатов используем информацию о группе
                        chatInfos.Add(new ChatInfoDto
                        {
                            Id = chat.Id,
                            Name = chat.Name,
                            DisplayName = chat.Name,
                            IsGroup = true,
                            Description = chat.Description ?? "Group chat",
                            OtherUserId = 0,
                            OtherUsername = "",
                            CreatedAt = chat.CreatedAt,
                            UnreadCount = 0,
                            LastMessage = lastMessage?.Content,
                            LastMessageTime = lastMessage?.SentAt,
                            Avatar = chat.Avatar
                        });
                    }
                    else
                    {
                        // Для приватных чатов используем информацию о другом пользователе
                        var otherMember = chat.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                        if (otherMember != null)
                        {
                            var otherUser = await _chatService.GetUserByIdAsync(otherMember.UserId);

                            chatInfos.Add(new ChatInfoDto
                            {
                                Id = chat.Id,
                                Name = chat.Name,
                                DisplayName = otherUser?.Username ?? "Unknown",
                                IsGroup = false,
                                Description = lastMessage?.Content ?? "Нет сообщений",
                                OtherUserId = otherMember.UserId,
                                OtherUsername = otherUser?.Username ?? "Unknown",
                                CreatedAt = chat.CreatedAt,
                                UnreadCount = 0,
                                LastMessage = lastMessage?.Content,
                                LastMessageTime = lastMessage?.SentAt,
                                Avatar = otherUser?.Avatar
                            });
                        }
                    }
                }

                        _logger.LogInformation("Sending {Count} chats to user {UserId}", chatInfos.Count, _currentUserId);
                        await SendResponseAsync(true, "User chats", chatInfos);
                        _logger.LogInformation("User chats response sent successfully");
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
                // Получаем всех участников чата из БД
                var chatRoom = await _chatService.GetChatRoomAsync(_currentChatRoomId);
                if (chatRoom == null)
                {
                    return;
                }

                // Отправляем сообщение всем участникам чата, даже если они не в комнате
                foreach (var member in chatRoom.Members)
                {
                    try
                    {
                        // Сначала пробуем отправить через соединения в комнате
                        var roomConnections = _connectionManager.GetRoomConnections(_currentChatRoomId);
                        var connectionInRoom = roomConnections.FirstOrDefault(c => c.CurrentUserId == member.UserId);
                        
                        if (connectionInRoom != null)
                        {
                            await connectionInRoom.SendMessageToClientAsync(messageDto);
                        }
                        else
                        {
                            // Если пользователь не в комнате, но онлайн, отправляем через его основное соединение
                            if (_connectionManager.IsUserOnline(member.UserId))
                            {
                                var userConnection = _connectionManager.GetUserConnection(member.UserId);
                                if (userConnection != null)
                                {
                                    await userConnection.SendMessageToClientAsync(messageDto);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send to user {UserId}", member.UserId);
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

                var isMember = chatRoom.Members.Any(m => m.UserId == _currentUserId);
                if (!isMember)
                {
                    await SendResponseAsync(false, "You are not a member of this chat");
                    return;
                }

                _currentChatRoomId = roomId;
                _connectionManager.JoinRoom(_currentUserId, _currentChatRoomId, this);

                var history = await _chatService.GetRoomMessagesAsync(roomId);

                if (chatRoom.IsGroup)
                {
                    // Для групповых чатов используем информацию о группе
                    await SendResponseAsync(true, $"Joined chat room", new
                    {
                        RoomId = chatRoom.Id,
                        TargetUser = chatRoom.Name,
                        OtherUserId = 0,
                        History = history
                    });
                }
                else
                {
                    // Для приватных чатов используем информацию о другом пользователе
                    var otherMember = chatRoom.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                    var otherUser = otherMember?.User;

                    await SendResponseAsync(true, $"Joined chat room", new
                    {
                        RoomId = chatRoom.Id,
                        TargetUser = otherUser?.Username ?? "Unknown",
                        OtherUserId = otherMember?.UserId ?? 0,
                        History = history
                    });
                }
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
                _logger.LogInformation("LoginAsync result: Success={Success}, Message={Message}, Data type={DataType}", 
                    result.Success, result.Message, result.Data?.GetType().Name ?? "null");
                
                if (result.Success)
                {
                    try
                    {
                        // Используем метод GetData<T>() из ApiResponse для получения UserProfileDto
                        UserProfileDto? userProfile = result.GetData<UserProfileDto>();
                        
                        if (userProfile == null)
                        {
                            // Если GetData не сработал, пробуем другие способы
                            if (result.Data is UserProfileDto directProfile)
                            {
                                userProfile = directProfile;
                                _logger.LogInformation("Got UserProfileDto directly: Id={Id}, Username={Username}", 
                                    userProfile.Id, userProfile.Username);
                            }
                            else if (result.Data is JsonElement jsonElement)
                            {
                                try
                                {
                                    userProfile = JsonSerializer.Deserialize<UserProfileDto>(jsonElement.GetRawText());
                                    _logger.LogInformation("Deserialized UserProfileDto from JsonElement: Id={Id}, Username={Username}", 
                                        userProfile?.Id ?? 0, userProfile?.Username ?? "null");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to deserialize UserProfileDto from login response: {Error}", ex.Message);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("result.Data is neither UserProfileDto nor JsonElement. Type: {Type}", 
                                    result.Data?.GetType().Name ?? "null");
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Got UserProfileDto via GetData<T>: Id={Id}, Username={Username}", 
                                userProfile.Id, userProfile.Username);
                        }
                        
                        if (userProfile != null)
                        {
                            _currentUserId = userProfile.Id;
                            _currentUsername = userProfile.Username;
                            _logger.LogInformation("Set _currentUserId={UserId}, _currentUsername={Username}", 
                                _currentUserId, _currentUsername);
                        }
                        else
                        {
                            _logger.LogWarning("userProfile is null, cannot set _currentUserId");
                        }

                        _logger.LogInformation("Before connection check: _currentUserId={UserId}", _currentUserId);
                        if (_currentUserId > 0)
                        {
                            _connectionManager.AddConnection(_currentUserId, this);

                            var userChats = await _chatService.GetUserChatRoomsAsync(_currentUserId);
                            var chatInfos = new List<ChatInfoDto>();

                            foreach (var chat in userChats)
                            {
                                var lastMessage = await _chatService.GetLastMessageAsync(chat.Id);

                                if (chat.IsGroup)
                                {
                                    // Для групповых чатов используем информацию о группе
                                    chatInfos.Add(new ChatInfoDto
                                    {
                                        Id = chat.Id,
                                        Name = chat.Name,
                                        DisplayName = chat.Name,
                                        IsGroup = true,
                                        Description = chat.Description ?? "Group chat",
                                        OtherUserId = 0,
                                        OtherUsername = "",
                                        CreatedAt = chat.CreatedAt,
                                        UnreadCount = 0,
                                        LastMessage = lastMessage?.Content,
                                        LastMessageTime = lastMessage?.SentAt,
                                        Avatar = chat.Avatar
                                    });
                                }
                                else
                                {
                                    // Для приватных чатов используем информацию о другом пользователе
                                    var otherMember = chat.Members.FirstOrDefault(m => m.UserId != _currentUserId);
                                    if (otherMember != null)
                                    {
                                        var otherUser = await _chatService.GetUserByIdAsync(otherMember.UserId);

                                        chatInfos.Add(new ChatInfoDto
                                        {
                                            Id = chat.Id,
                                            Name = chat.Name,
                                            DisplayName = otherUser?.Username ?? "Unknown",
                                            IsGroup = false,
                                            Description = lastMessage?.Content ?? "Нет сообщений",
                                            OtherUserId = otherMember.UserId,
                                            OtherUsername = otherUser?.Username ?? "Unknown",
                                            CreatedAt = chat.CreatedAt,
                                            UnreadCount = 0,
                                            LastMessage = lastMessage?.Content,
                                            LastMessageTime = lastMessage?.SentAt,
                                            Avatar = otherUser?.Avatar
                                        });
                                    }
                                }
                            }

                            // Отправляем полные данные пользователя
                            // Используем UserProfileDto для совместимости с клиентом
                            var userProfileForResponse = userProfile ?? new UserProfileDto
                            {
                                Id = _currentUserId,
                                Username = string.IsNullOrEmpty(_currentUsername) ? $"user_{_currentUserId}" : _currentUsername,
                                DisplayName = "",
                                ProfileInfo = "",
                                Theme = "Latte",
                                Avatar = null
                            };
                            
                            // Дополнительная проверка на пустой Username
                            if (string.IsNullOrEmpty(userProfileForResponse.Username))
                            {
                                _logger.LogWarning("Username is empty for user {UserId}, using fallback", _currentUserId);
                                userProfileForResponse.Username = $"user_{_currentUserId}";
                            }

                            _logger.LogInformation("Sending login response with UserId={UserId}, Username={Username}, ChatsCount={ChatsCount}", 
                                userProfileForResponse.Id, userProfileForResponse.Username, chatInfos.Count);
                            
                            // Отправляем UserProfileDto напрямую в Data, чтобы клиент мог его десериализовать
                            await SendResponseAsync(true, "Login successful", userProfileForResponse);
                            _logger.LogInformation("Login response sent successfully");
                        }
                        else
                        {
                            _logger.LogWarning("Cannot send login response: _currentUserId is 0");
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

        private async Task RegisterUserAsync(string username, string password, string displayName)
        {
            var result = await _authService.RegisterAsync(username, password, displayName);
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

        private async Task HandleUploadFileAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 5)
                {
                    await SendResponseAsync(false, "Usage: /upload_file <roomId> <fileName> <fileSize> <messageType>");
                    return;
                }

                if (!int.TryParse(parts[1], out int roomId))
                {
                    await SendResponseAsync(false, "Invalid room ID");
                    return;
                }

                if (!long.TryParse(parts[3], out long fileSize))
                {
                    await SendResponseAsync(false, "Invalid file size");
                    return;
                }

                if (!Enum.TryParse<MessageType>(parts[4], true, out MessageType messageType))
                {
                    await SendResponseAsync(false, "Invalid message type");
                    return;
                }

                string fileName = parts[2];
                var resolvedType = ResolveMessageType(fileName, messageType);

                // Check if user is member of the room
                var room = await _chatService.GetChatRoomAsync(roomId);
                if (room == null || !room.Members.Any(m => m.UserId == _currentUserId))
                {
                    await SendResponseAsync(false, "You are not a member of this room");
                    return;
                }

                // Send ready response
                await SendResponseAsync(true, "Ready to receive file");

                // Read file data
                byte[] fileData = new byte[fileSize];
                int totalBytesRead = 0;
                int bytesRead;

                while (totalBytesRead < fileSize)
                {
                    int bytesToRead = (int)Math.Min(81920, fileSize - totalBytesRead);
                    bytesRead = await _stream!.ReadAsync(fileData, totalBytesRead, bytesToRead);
                    if (bytesRead == 0)
                    {
                        await SendResponseAsync(false, "Connection lost during file upload");
                        return;
                    }
                    totalBytesRead += bytesRead;
                }

                // Save file
                string uniqueFileName = await _fileStorageService.SaveFileAsync(fileData, fileName, messageType);

                // Determine MIME type
                string mimeType = GetMimeType(fileName);

                // Save message to database
                var messageDto = await _chatService.SaveFileMessageAsync(
                    _currentUserId,
                    roomId,
                    uniqueFileName,
                    fileName,
                    mimeType,
                    fileSize,
                    resolvedType
                );

                if (messageDto == null)
                {
                    await SendResponseAsync(false, "Failed to save message");
                    return;
                }

                // Broadcast message to room members
                _currentChatRoomId = roomId;
                await BroadcastToChatRoomAsync(messageDto);

                // Send success response
                await SendResponseAsync(true, "File uploaded successfully", messageDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file upload");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleDownloadFileAsync(string uniqueFileName)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (!_fileStorageService.FileExists(uniqueFileName))
                {
                    await SendResponseAsync(false, "File not found");
                    return;
                }

                string filePath = _fileStorageService.GetFilePath(uniqueFileName);
                FileInfo fileInfo = new FileInfo(filePath);

                var metadata = new FileDownloadMetadata
                {
                    FileSize = fileInfo.Length,
                    FileName = uniqueFileName
                };

                // Send metadata
                var headerResponse = new ApiResponse
                {
                    Success = true,
                    Message = "FILE_TRANSFER_START",
                    Data = metadata
                };

                await SendResponseAsync(headerResponse);

                const int chunkSize = 64 * 1024;
                byte[] buffer = new byte[chunkSize];
                int chunkIndex = 0;

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunkDto = new FileChunkDto
                        {
                            FileName = uniqueFileName,
                            ChunkIndex = chunkIndex++,
                            BytesLength = bytesRead,
                            Data = Convert.ToBase64String(buffer, 0, bytesRead)
                        };

                        await SendResponseAsync(true, "FILE_TRANSFER_CHUNK", chunkDto);
                    }
                }

                await SendResponseAsync(true, "FILE_TRANSFER_COMPLETE", new { FileName = uniqueFileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file download");
                await SendResponseAsync(false, "FILE_TRANSFER_FAILED", new { FileName = uniqueFileName, Reason = ex.Message });
            }
        }

        private async Task HandleDownloadInlineAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 2)
                {
                    await SendResponseAsync(false, "Usage: /download_inline <fileName>");
                    return;
                }

                var uniqueFileName = parts[1];
                if (!_fileStorageService.FileExists(uniqueFileName))
                {
                    await SendResponseAsync(false, "File not found");
                    return;
                }

                var path = _fileStorageService.GetFilePath(uniqueFileName);
                var info = new FileInfo(path);
                const long inlineLimit = 20 * 1024 * 1024; // 20MB cap
                if (info.Length > inlineLimit)
                {
                    await SendResponseAsync(false, "File too large for inline download");
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(path);
                var dto = new InlineFileDto
                {
                    FileName = uniqueFileName,
                    Data = Convert.ToBase64String(bytes)
                };

                await SendResponseAsync(true, "INLINE_FILE", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling inline download");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleEditMessageAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 3)
                {
                    await SendResponseAsync(false, "Usage: /edit_message <messageId> <newContent>");
                    return;
                }

                if (!int.TryParse(parts[1], out int messageId))
                {
                    await SendResponseAsync(false, "Invalid message ID");
                    return;
                }

                // Получаем новое содержимое (объединяем все части после messageId)
                string newContent = string.Join(" ", parts.Skip(2));

                // Получаем сообщение для проверки владельца
                var message = await _chatService.GetMessageAsync(messageId);
                if (message == null)
                {
                    await SendResponseAsync(false, "Message not found");
                    return;
                }

                // Проверяем, что сообщение принадлежит текущему пользователю
                if (message.UserId != _currentUserId)
                {
                    await SendResponseAsync(false, "You can only edit your own messages");
                    return;
                }

                // Редактируем сообщение
                bool success = await _chatService.EditMessageAsync(messageId, newContent);
                if (!success)
                {
                    await SendResponseAsync(false, "Failed to edit message");
                    return;
                }

                // Получаем обновленное сообщение
                var updatedMessage = await _chatService.GetMessageAsync(messageId);
                if (updatedMessage == null)
                {
                    await SendResponseAsync(false, "Failed to retrieve updated message");
                    return;
                }

                // Отправляем успешный ответ
                await SendResponseAsync(true, "Message edited successfully", updatedMessage);

                // Отправляем обновление всем пользователям в чате
                await BroadcastMessageUpdateAsync(updatedMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling edit message");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleDeleteMessageAsync(string[] parts)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (parts.Length < 2)
                {
                    await SendResponseAsync(false, "Usage: /delete_message <messageId>");
                    return;
                }

                if (!int.TryParse(parts[1], out int messageId))
                {
                    await SendResponseAsync(false, "Invalid message ID");
                    return;
                }

                // Получаем сообщение для проверки владельца и получения ChatRoomId
                var message = await _chatService.GetMessageAsync(messageId);
                if (message == null)
                {
                    await SendResponseAsync(false, "Message not found");
                    return;
                }

                // Проверяем, что сообщение принадлежит текущему пользователю
                if (message.UserId != _currentUserId)
                {
                    await SendResponseAsync(false, "You can only delete your own messages");
                    return;
                }

                int chatRoomId = message.ChatRoomId;

                // Удаляем сообщение
                bool success = await _chatService.DeleteMessageAsync(messageId);
                if (!success)
                {
                    await SendResponseAsync(false, "Failed to delete message");
                    return;
                }

                // Отправляем успешный ответ
                var deleteResponse = new ApiResponse
                {
                    Success = true,
                    Message = "Message deleted successfully",
                    Data = new { MessageId = messageId, ChatRoomId = chatRoomId }
                };
                await SendResponseAsync(deleteResponse);

                // Отправляем уведомление об удалении всем пользователям в чате
                await BroadcastMessageDeleteAsync(messageId, chatRoomId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling delete message");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task BroadcastMessageUpdateAsync(MessageDto messageDto)
        {
            try
            {
                var roomConnections = _connectionManager.GetRoomConnections(messageDto.ChatRoomId);
                foreach (var connection in roomConnections)
                {
                    try
                    {
                        // Отправляем специальное уведомление об обновлении сообщения
                        var updateResponse = new ApiResponse
                        {
                            Success = true,
                            Message = "Message updated",
                            Data = messageDto
                        };
                        await connection.SendResponseAsync(updateResponse);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send message update to user");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BroadcastMessageUpdateAsync");
            }
        }

        private async Task BroadcastMessageDeleteAsync(int messageId, int chatRoomId)
        {
            try
            {
                var roomConnections = _connectionManager.GetRoomConnections(chatRoomId);
                foreach (var connection in roomConnections)
                {
                    try
                    {
                        // Отправляем специальное уведомление об удалении сообщения
                        var deleteResponse = new ApiResponse
                        {
                            Success = true,
                            Message = "Message deleted",
                            Data = new { MessageId = messageId, ChatRoomId = chatRoomId }
                        };
                        await connection.SendResponseAsync(deleteResponse);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send message delete notification to user");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BroadcastMessageDeleteAsync");
            }
        }

        private MessageType ResolveMessageType(string fileName, MessageType requestedType)
        {
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return requestedType == MessageType.Text ? MessageType.File : requestedType;
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

            return requestedType switch
            {
                MessageType.Image => MessageType.Image,
                MessageType.Audio => MessageType.Audio,
                MessageType.Video => MessageType.Video,
                _ => MessageType.File
            };
        }

        private string GetMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
        }

        // Методы обработки групп
        private async Task HandleCreateGroupAsync(string groupName)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                if (string.IsNullOrWhiteSpace(groupName))
                {
                    await SendResponseAsync(false, "Group name cannot be empty");
                    return;
                }

                var group = await _chatService.CreateGroupAsync(_currentUserId, groupName.Trim());
                
                var groupDto = new ChatInfoDto
                {
                    Id = group.Id,
                    Name = group.Name,
                    DisplayName = group.Name,
                    IsGroup = true,
                    Description = group.Description,
                    Avatar = group.Avatar,
                    CreatedAt = group.CreatedAt,
                    UnreadCount = 0
                };

                var response = new ApiResponse
                {
                    Success = true,
                    Message = "Group created successfully",
                    Data = groupDto
                };

                await SendResponseAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating group");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleLeaveGroupAsync(int groupId)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var success = await _chatService.LeaveGroupAsync(_currentUserId, groupId);
                
                if (success)
                {
                    var response = new ApiResponse
                    {
                        Success = true,
                        Message = "Left group successfully",
                        Data = new { GroupId = groupId }
                    };
                    await SendResponseAsync(response);
                }
                else
                {
                    await SendResponseAsync(false, "Failed to leave group or group not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error leaving group");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleGetGroupInfoAsync(int groupId)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var group = await _chatService.GetGroupInfoAsync(groupId);
                
                if (group == null)
                {
                    await SendResponseAsync(false, "Group not found");
                    return;
                }

                // Проверяем, что пользователь является участником группы
                var isMember = group.Members.Any(m => m.UserId == _currentUserId);
                if (!isMember)
                {
                    await SendResponseAsync(false, "You are not a member of this group");
                    return;
                }

                // Формируем список участников
                var members = group.Members.Select(m => new
                {
                    Id = m.UserId,
                    Username = m.User?.Username ?? "Unknown",
                    DisplayName = m.User?.DisplayName ?? "",
                    Avatar = m.User?.Avatar,
                    IsAdmin = m.IsAdmin
                }).ToArray();

                var groupInfo = new
                {
                    Id = group.Id,
                    Name = group.Name,
                    Description = group.Description,
                    Avatar = group.Avatar,
                    CreatedAt = group.CreatedAt,
                    Members = members
                };

                var response = new ApiResponse
                {
                    Success = true,
                    Message = "Group info",
                    Data = groupInfo
                };

                await SendResponseAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting group info");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task HandleUpdateGroupAsync(int groupId, string[] updateParams)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                string? newName = null;
                string? newDescription = null;

                foreach (var param in updateParams)
                {
                    if (param.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        newName = param.Substring(5);
                    }
                    else if (param.StartsWith("desc:", StringComparison.OrdinalIgnoreCase))
                    {
                        newDescription = param.Substring(5);
                    }
                }

                var updatedGroup = await _chatService.UpdateGroupAsync(groupId, _currentUserId, newName, newDescription);
                
                if (updatedGroup == null)
                {
                    await SendResponseAsync(false, "Failed to update group or you don't have permission");
                    return;
                }

                // Формируем ответ с обновленной информацией о группе
                var members = updatedGroup.Members.Select(m => new
                {
                    Id = m.UserId,
                    Username = m.User?.Username ?? "Unknown",
                    DisplayName = m.User?.DisplayName ?? "",
                    Avatar = m.User?.Avatar,
                    IsAdmin = m.IsAdmin
                }).ToArray();

                var groupInfo = new
                {
                    Id = updatedGroup.Id,
                    Name = updatedGroup.Name,
                    Description = updatedGroup.Description,
                    Avatar = updatedGroup.Avatar,
                    CreatedAt = updatedGroup.CreatedAt,
                    Members = members
                };

                var response = new ApiResponse
                {
                    Success = true,
                    Message = "Group updated",
                    Data = groupInfo
                };

                await SendResponseAsync(response);

                // Broadcast обновление группы всем участникам
                await BroadcastGroupUpdateAsync(updatedGroup);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating group");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }

        private async Task BroadcastGroupUpdateAsync(ChatRoom group)
        {
            try
            {
                var members = group.Members.Select(m => new
                {
                    Id = m.UserId,
                    Username = m.User?.Username ?? "Unknown",
                    DisplayName = m.User?.DisplayName ?? "",
                    Avatar = m.User?.Avatar,
                    IsAdmin = m.IsAdmin
                }).ToArray();

                var groupInfo = new
                {
                    Id = group.Id,
                    Name = group.Name,
                    Description = group.Description,
                    Avatar = group.Avatar,
                    CreatedAt = group.CreatedAt,
                    Members = members
                };

                var updateResponse = new ApiResponse
                {
                    Success = true,
                    Message = "Group updated",
                    Data = groupInfo
                };

                foreach (var member in group.Members)
                {
                    var handler = _connectionManager.GetUserConnection(member.UserId);
                    if (handler != null && handler != this)
                    {
                        await handler.SendResponseAsync(updateResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting group update");
            }
        }

        private async Task HandleAddMemberAsync(int groupId, string username)
        {
            try
            {
                if (_currentUserId == 0)
                {
                    await SendResponseAsync(false, "You must login first");
                    return;
                }

                var result = await _chatService.AddMemberToGroupAsync(groupId, _currentUserId, username);
                
                if (result.Success)
                {
                    // Перезагружаем информацию о группе для отправки обновленного списка участников
                    var group = await _chatService.GetGroupInfoAsync(groupId);
                    if (group != null)
                    {
                        var members = group.Members.Select(m => new
                        {
                            Id = m.UserId,
                            Username = m.User?.Username ?? "Unknown",
                            DisplayName = m.User?.DisplayName ?? "",
                            Avatar = m.User?.Avatar,
                            IsAdmin = m.IsAdmin
                        }).ToArray();

                        var groupInfo = new
                        {
                            Id = group.Id,
                            Name = group.Name,
                            Description = group.Description,
                            Avatar = group.Avatar,
                            CreatedAt = group.CreatedAt,
                            Members = members
                        };

                        var response = new ApiResponse
                        {
                            Success = true,
                            Message = "Member added successfully",
                            Data = groupInfo
                        };

                        await SendResponseAsync(response);

                        // Broadcast обновление группы всем участникам
                        await BroadcastGroupUpdateAsync(group);
                    }
                }
                else
                {
                    await SendResponseAsync(false, result.Message ?? "Failed to add member");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding member to group");
                await SendResponseAsync(false, $"Error: {ex.Message}");
            }
        }
    }
}