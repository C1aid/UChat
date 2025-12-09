using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using uchat_server.Data;
using Uchat.Shared.DTOs;
using Uchat.Shared.Models;
using Uchat.Shared.Enums;

namespace uchat_server.Services
{
    public class ChatService
    {
        private readonly ChatContext _context;
        private readonly ILogger<ChatService> _logger;
        private readonly ConnectionManager _connectionManager;

        public ChatService(ChatContext context, ILogger<ChatService> logger, ConnectionManager connectionManager)
        {
            _context = context;
            _logger = logger;
            _connectionManager = connectionManager;
        }

        public async Task<User?> GetUserByUsernameAsync(string username)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Username == username);
        }

        public async Task<User?> GetUserByIdAsync(int userId)
        {
            return await _context.Users.FindAsync(userId);
        }


        public async Task<ChatRoom?> GetChatRoomAsync(int roomId)
        {
            return await _context.ChatRooms
                .AsNoTracking()
                .Include(c => c.Members)
                    .ThenInclude(m => m.User)
                .FirstOrDefaultAsync(c => c.Id == roomId);
        }

        public async Task<ChatRoom> GetOrCreatePrivateChatAsync(int userId1, int userId2)
        {
            var existingChat = await _context.ChatRooms
                .Include(c => c.Members)
                .Where(c => !c.IsGroup)
                .Where(c => c.Members.Any(m => m.UserId == userId1) && c.Members.Any(m => m.UserId == userId2))
                .FirstOrDefaultAsync();

            if (existingChat != null)
            {
                return existingChat;
            }

            var user1 = await _context.Users.FindAsync(userId1);
            var user2 = await _context.Users.FindAsync(userId2);

            if (user1 == null || user2 == null)
            {
                throw new Exception("One or both users not found");
            }

            var chatRoom = new ChatRoom
            {
                Name = $"Private_{userId1}_{userId2}",
                IsGroup = false,
                Description = $"Private chat between {user1.Username} and {user2.Username}",
                CreatedAt = DateTime.UtcNow
            };

            _context.ChatRooms.Add(chatRoom);
            await _context.SaveChangesAsync();

            var member1 = new ChatRoomMember
            {
                ChatRoomId = chatRoom.Id,
                UserId = userId1,
                IsAdmin = false
            };

            var member2 = new ChatRoomMember
            {
                ChatRoomId = chatRoom.Id,
                UserId = userId2,
                IsAdmin = false
            };

            _context.ChatRoomMembers.Add(member1);
            _context.ChatRoomMembers.Add(member2);

            await _context.SaveChangesAsync();

            int recipientId = userId2;
            if (_connectionManager.IsUserOnline(recipientId))
            {
                var recipientHandler = _connectionManager.GetUserConnection(recipientId);

                if (recipientHandler != null)
                {
                    var notificationDto = new MessageDto 
                    {
                        MessageType = MessageType.NewChatNotification,
                        ChatRoomId = chatRoom.Id,
                        Content = chatRoom.Description 
                    };
                    await recipientHandler.SendDtoAsync(notificationDto); 
                }
            }

            return chatRoom;
        }

        public async Task<ChatRoom[]> GetUserChatRoomsAsync(int userId)
        {
            return await _context.ChatRooms
                .Include(c => c.Members)
                    .ThenInclude(m => m.User)
                .Where(c => c.Members.Any(m => m.UserId == userId))
                .ToArrayAsync();
        }

        public async Task<MessageDto> SaveMessageAsync(int userId, int chatRoomId, string content)
        {
            var message = new Message
            {
                Content = content,
                SentAt = DateTime.UtcNow,
                UserId = userId,
                ChatRoomId = chatRoomId,
                MessageType = MessageType.Text
            };

            _context.Messages.Add(message);
            await _context.SaveChangesAsync();

            var user = await _context.Users.FindAsync(userId);
            var username = user?.Username ?? "Unknown";

            return new MessageDto
            {
                Id = message.Id,
                Content = message.Content,
                SentAt = message.SentAt,
                UserId = message.UserId,
                Username = username,
                ChatRoomId = message.ChatRoomId,
                MessageType = message.MessageType,
                Avatar = user?.Avatar,
                FileUrl = message.FileUrl ?? string.Empty,
                FileName = message.FileName ?? string.Empty,
                MimeType = message.MimeType ?? string.Empty,
                FileSize = message.FileSize
            };
        }

        public async Task<MessageDto> SaveFileMessageAsync(int userId, int chatRoomId, string fileUrl, 
            string fileName, string mimeType, long fileSize, MessageType messageType)
        {
            var message = new Message
            {
                Content = fileName,
                SentAt = DateTime.UtcNow,
                UserId = userId,
                ChatRoomId = chatRoomId,
                MessageType = messageType,
                FileUrl = fileUrl,
                FileName = fileName,
                MimeType = mimeType,
                FileSize = fileSize
            };

            _context.Messages.Add(message);
            await _context.SaveChangesAsync();

            var user = await _context.Users.FindAsync(userId);
            var username = user?.Username ?? "Unknown";

            return new MessageDto
            {
                Id = message.Id,
                Content = message.Content,
                SentAt = message.SentAt,
                UserId = message.UserId,
                Username = username,
                ChatRoomId = message.ChatRoomId,
                MessageType = message.MessageType,
                Avatar = user?.Avatar,
                FileUrl = message.FileUrl ?? string.Empty,
                FileName = message.FileName ?? string.Empty,
                MimeType = message.MimeType ?? string.Empty,
                FileSize = message.FileSize
            };
        }

        public async Task<MessageDto[]> GetRoomMessagesAsync(int chatRoomId, int limit = 100)
        {
            var messages = await _context.Messages
                .AsNoTracking()
                .Include(m => m.User)
                .Where(m => m.ChatRoomId == chatRoomId)
                .OrderByDescending(m => m.SentAt)
                .Take(limit)
                .ToArrayAsync();

            return messages
                .Reverse()
                .Select(m => new MessageDto
                {
                    Id = m.Id,
                    Content = m.Content,
                    SentAt = m.SentAt,
                    EditedAt = m.EditedAt,
                    UserId = m.UserId,
                    Username = m.User?.Username ?? "Unknown",
                    ChatRoomId = m.ChatRoomId,
                    MessageType = m.MessageType,
                    Avatar = m.User?.Avatar,
                    FileUrl = m.FileUrl ?? string.Empty,
                    FileName = m.FileName ?? string.Empty,
                    MimeType = m.MimeType ?? string.Empty,
                    FileSize = m.FileSize
                }).ToArray();
        }

        public async Task<bool> IsUserInRoomAsync(int userId, int chatRoomId)
        {
            return await _context.ChatRoomMembers
                .AnyAsync(m => m.UserId == userId && m.ChatRoomId == chatRoomId);
        }

        public async Task<MessageDto?> GetLastMessageAsync(int chatRoomId)
        {
            var message = await _context.Messages
                .Include(m => m.User)
                .Where(m => m.ChatRoomId == chatRoomId)
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefaultAsync();

            if (message == null)
                return null;

            return new MessageDto
            {
                Id = message.Id,
                Content = message.Content,
                SentAt = message.SentAt,
                EditedAt = message.EditedAt,
                UserId = message.UserId,
                Username = message.User?.Username ?? "Unknown",
                ChatRoomId = message.ChatRoomId,
                MessageType = message.MessageType,
                Avatar = message.User?.Avatar,
                FileUrl = message.FileUrl ?? string.Empty,
                FileName = message.FileName ?? string.Empty,
                MimeType = message.MimeType ?? string.Empty,
                FileSize = message.FileSize
            };
        }

        public async Task<bool> DeleteMessageAsync(int messageId)
        {
            var message = await _context.Messages.FindAsync(messageId);
            if (message == null)
                return false;

            _context.Messages.Remove(message);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteChatAsync(int chatRoomId, int userId)
        {
            try
            {
                // Используем AsNoTracking для чтения, чтобы избежать проблем с отслеживанием
                var chatRoom = await _context.ChatRooms
                    .AsNoTracking()
                    .Include(c => c.Members)
                    .FirstOrDefaultAsync(c => c.Id == chatRoomId);

                if (chatRoom == null)
                {
                    _logger.LogWarning("Chat room not found: {ChatRoomId}", chatRoomId);
                    return false;
                }

                // Проверяем, что пользователь является участником чата
                var isMember = chatRoom.Members.Any(m => m.UserId == userId);
                if (!isMember)
                {
                    _logger.LogWarning("User {UserId} is not a member of chat {ChatRoomId}", userId, chatRoomId);
                    return false;
                }

                // Для приватных чатов удаляем чат полностью (для обоих пользователей)
                if (!chatRoom.IsGroup)
                {
                    // Удаляем всех участников напрямую через запрос
                    var membersToDelete = await _context.ChatRoomMembers
                        .Where(crm => crm.ChatRoomId == chatRoomId)
                        .ToListAsync();
                    
                    if (membersToDelete.Any())
                    {
                        _context.ChatRoomMembers.RemoveRange(membersToDelete);
                    }

                    // Удаляем все сообщения в чате напрямую через запрос
                    var messagesToDelete = await _context.Messages
                        .Where(m => m.ChatRoomId == chatRoomId)
                        .ToListAsync();
                    
                    if (messagesToDelete.Any())
                    {
                        _context.Messages.RemoveRange(messagesToDelete);
                    }

                    // Удаляем сам чат (загружаем заново для отслеживания)
                    var chatRoomToDelete = await _context.ChatRooms.FindAsync(chatRoomId);
                    if (chatRoomToDelete != null)
                    {
                        _context.ChatRooms.Remove(chatRoomToDelete);
                    }
                }
                else
                {
                    // Для групповых чатов удаляем только членство пользователя
                    var userMembership = await _context.ChatRoomMembers
                        .FirstOrDefaultAsync(crm => crm.ChatRoomId == chatRoomId && crm.UserId == userId);
                    
                    if (userMembership != null)
                    {
                        _context.ChatRoomMembers.Remove(userMembership);
                    }
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation("Chat {ChatRoomId} deleted by user {UserId}", chatRoomId, userId);
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "Concurrency exception when deleting chat {ChatRoomId} for user {UserId}. Chat may have already been deleted.", chatRoomId, userId);
                // Проверяем, действительно ли чат был удален
                var stillExists = await _context.ChatRooms.AnyAsync(c => c.Id == chatRoomId);
                return !stillExists; // Возвращаем true, если чат больше не существует
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting chat {ChatRoomId} for user {UserId}", chatRoomId, userId);
                return false;
            }
        }

        public async Task<bool> EditMessageAsync(int messageId, string newContent)
        {
            var message = await _context.Messages.FindAsync(messageId);
            if (message == null)
                return false;

            message.Content = newContent;
            message.EditedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<MessageDto?> GetMessageAsync(int messageId)
        {
            var message = await _context.Messages
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.Id == messageId);

            if (message == null)
                return null;

            return new MessageDto
            {
                Id = message.Id,
                Content = message.Content,
                SentAt = message.SentAt,
                EditedAt = message.EditedAt,
                UserId = message.UserId,
                Username = message.User?.Username ?? "Unknown",
                ChatRoomId = message.ChatRoomId,
                MessageType = message.MessageType,
                FileUrl = message.FileUrl ?? string.Empty,
                FileName = message.FileName ?? string.Empty,
                MimeType = message.MimeType ?? string.Empty,
                FileSize = message.FileSize
            };
        }
    }
}