using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        private readonly ILogger<ClientHandler> _logger;
        private readonly FileStorageService _fileStorageService;
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
                           ConnectionManager connectionManager, ILogger<ClientHandler> logger,
                           FileStorageService fileStorageService)
        {
            _client = client;
            _authService = authService;
            _chatService = chatService;
            _connectionManager = connectionManager;
            _logger = logger;
            _fileStorageService = fileStorageService;
        }

        public async Task StartAsync()
        {
            try
            {
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

                await SendWelcomeMessage();

                while (true)
                {
                    if (_reader == null)
                    {
                        await Task.Delay(50);
                        continue;
                    }
                    
                    string? message;
                    try
                    {
                        message = await _reader.ReadLineAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        await Task.Delay(50);
                        continue;
                    }
                    
                    if (message == null)
                    {
                        break;
                    }
                    
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
            catch (IOException ioEx)
            {
                _logger.LogWarning(ioEx, "Connection closed by client");
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
            try
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
                        _logger.LogInformation("Attempting registration command.");
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
                    
                    case "/upload_file":
                        if (parts.Length != 5 || 
                            !int.TryParse(parts[1], out int uploadRoomId) || 
                            !long.TryParse(parts[3], out long fileSize))
                        {
                            await SendResponseAsync(false, "Invalid upload format. Usage: /upload_file <roomId> <fileName> <fileSize> <type>");
                            return;
                        }
                        string fileName = parts[2];
                        string typeString = parts[4];
                        
                        if (!Enum.TryParse(typeString, true, out MessageType messageType))
                        {
                            await SendResponseAsync(false, $"Invalid MessageType: {typeString}.");
                            return;
                        }

                        await HandleFileUploadAsync(uploadRoomId, fileName, fileSize, messageType);
                        break;

                    case "/download":
                        if (parts.Length != 2)
                        {
                            await SendResponseAsync(false, "Usage: /download <uniqueFileName>");
                            return;
                        }
                        string uniqueFileName = parts[1];
                        await HandleFileDownloadAsync(uniqueFileName);
                        break;

                    default:
                        await SendResponseAsync(false, $"Unknown command: {command}");
                        break;
                }
            }
            catch (Exception ex)
            {

                _logger.LogError(ex, $"Unhandled exception during command processing: {message}");
                await SendResponseAsync(false, "Server error processing command.");
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
            try
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
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "Database integrity error during registration.");
                await SendResponseAsync(false, "Registration failed: A database integrity error occurred."); 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during registration.");
                await SendResponseAsync(false, "Operation failed: Internal server error.");
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
                    await _writer.FlushAsync();
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

        private async Task HandleFileUploadAsync(int roomId, string fileName, long fileSize, MessageType messageType)
        {
            string extension = Path.GetExtension(fileName);
            string uniqueFileName = $"{Guid.NewGuid()}{extension}";
            string savePath = _fileStorageService.GetFilePath(uniqueFileName); 

            try
            {
                _logger.LogInformation($"[FileUpload] Starting upload: {fileName} ({fileSize} bytes) to room {roomId}");

                var readyResponse = new ApiResponse { Success = true, Message = "Ready for binary data" };
                string readyJson = JsonSerializer.Serialize(readyResponse) + "\n";
                
                if (_writer != null)
                {
                    await _writer.WriteLineAsync(readyJson.TrimEnd());
                    await _writer.FlushAsync();
                    _logger.LogInformation($"[FileUpload] Sent ready response");
                }

                var stream = _stream!;
                _reader = null;
                _writer = null;

                byte[] buffer = new byte[81920]; 
                long totalBytesRead = 0;
                int bytesRead;

                _logger.LogInformation($"[FileUpload] Starting to read {fileSize} bytes from stream");

                using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                {
                    while (totalBytesRead < fileSize)
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytesRead);
                        
                        try
                        {
                            bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);
                        }
                        catch (Exception readEx)
                        {
                            _logger.LogError(readEx, $"[FileUpload] Error reading from stream at {totalBytesRead}/{fileSize}");
                            throw;
                        }
                        
                        if (bytesRead == 0)
                        {
                            _logger.LogError($"[FileUpload] Connection closed. Read {totalBytesRead}/{fileSize} bytes");
                            throw new IOException($"Connection lost during upload. Read {totalBytesRead}/{fileSize} bytes.");
                        }

                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;
                        
                        if (totalBytesRead % (1024 * 1024) == 0)
                        {
                            _logger.LogInformation($"[FileUpload] Progress: {totalBytesRead}/{fileSize} bytes");
                        }
                    }
                }
                
                _logger.LogInformation($"[FileUpload] File received completely ({totalBytesRead} bytes), saving to DB");
                
                string mimeType = _chatService.GetMimeType(fileName);
                
                var messageDto = new MessageDto
                {
                    ChatRoomId = roomId,
                    UserId = _currentUserId, 
                    Username = _currentUsername,
                    MessageType = messageType,
                    FileUrl = uniqueFileName,
                    FileName = fileName,
                    MimeType = mimeType,
                    FileSize = fileSize,
                    Content = $"Sent a {messageType} ({fileName})",
                    SentAt = DateTime.UtcNow
                };
                
                var messageEntity = new Message
                {
                    Content = messageDto.Content,
                    SentAt = DateTime.UtcNow,
                    UserId = messageDto.UserId,
                    ChatRoomId = messageDto.ChatRoomId,
                    MessageType = messageDto.MessageType,
                    FileUrl = messageDto.FileUrl,
                    FileName = messageDto.FileName,
                    MimeType = messageDto.MimeType,
                    FileSize = messageDto.FileSize
                };
                await _chatService.SaveFileMessageAsync(messageEntity);
                messageDto.Id = messageEntity.Id;
                
                _logger.LogInformation($"[FileUpload] File saved to DB");
                
                var completeResponse = new ApiResponse 
                { 
                    Success = true, 
                    Message = "File upload complete.",
                    Data = messageDto
                };
                string completeJson = JsonSerializer.Serialize(completeResponse) + "\n";
                byte[] completeBytes = Encoding.UTF8.GetBytes(completeJson);
                await stream.WriteAsync(completeBytes, 0, completeBytes.Length);
                await stream.FlushAsync();
                
                _logger.LogInformation($"[FileUpload] Sent completion response with MessageDto");
                
                _reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream!, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                
                _logger.LogInformation($"[FileUpload] Reader/Writer recreated");
                
                await Task.Delay(100);
                
                await _chatService.BroadcastFileMessageAsync(messageDto);
                
                _logger.LogInformation($"[FileUpload] Broadcasted to room (excluding sender)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FileUpload] Error handling file upload for {fileName}");
                if (File.Exists(savePath)) 
                {
                    File.Delete(savePath);
                    _logger.LogInformation($"[FileUpload] Deleted incomplete file");
                }
                
                _reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream!, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                
                try
                {
                    var errorResponse = new ApiResponse { Success = false, Message = ex.Message };
                    string errorJson = JsonSerializer.Serialize(errorResponse);
                    
                    if (_writer != null)
                    {
                        await _writer.WriteLineAsync(errorJson);
                        await _writer.FlushAsync();
                    }
                }
                catch
                {
                    _logger.LogWarning($"[FileUpload] Could not send error response - connection may be broken");
                }
            }
        }

        private async Task HandleFileDownloadAsync(string uniqueFileName)
        {
            if (!_fileStorageService.FileExists(uniqueFileName))
            {
                await SendResponseAsync(false, "File not found.");
                return;
            }

            try
            {
                string filePath = _fileStorageService.GetFilePath(uniqueFileName);
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);

                var headerResponse = new ApiResponse
                {
                    Success = true,
                    Message = "FILE_TRANSFER_START",
                    Data = new { FileSize = fileBytes.Length, FileName = Path.GetFileName(filePath) }
                };
                string headerJson = JsonSerializer.Serialize(headerResponse) + "\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson);

                var stream = _stream!;
                _reader = null;
                _writer = null;

                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.FlushAsync();
                _logger.LogInformation($"[FileDownload] Header sent, now sending {fileBytes.Length} bytes");

                await stream.WriteAsync(fileBytes, 0, fileBytes.Length);
                await stream.FlushAsync();
                _logger.LogInformation($"[FileDownload] Sent {fileBytes.Length} bytes for {uniqueFileName}");

                await Task.Delay(100);

                _reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream!, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                _logger.LogInformation($"[FileDownload] Reader/Writer recreated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling file download for {uniqueFileName}.");
                
                if (_stream != null)
                {
                    _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                    _writer = new StreamWriter(_stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                }
                
                try
                {
                    await SendResponseAsync(false, "Server error during file download.");
                }
                catch { }
            }
        }

    }
}