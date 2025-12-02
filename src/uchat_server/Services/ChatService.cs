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

        public ChatService(ChatContext context, ILogger<ChatService> logger)
        {
            _context = context;
            _logger = logger;
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
                MessageType = (int)message.MessageType
            };
        }

        public async Task<MessageDto[]> GetRoomMessagesAsync(int chatRoomId)
        {
            var messages = await _context.Messages
                .Include(m => m.User)
                .Where(m => m.ChatRoomId == chatRoomId)
                .OrderBy(m => m.SentAt)
                .ToArrayAsync();

            return messages.Select(m => new MessageDto
            {
                Id = m.Id,
                Content = m.Content,
                SentAt = m.SentAt,
                EditedAt = m.EditedAt,
                UserId = m.UserId,
                Username = m.User?.Username ?? "Unknown",
                ChatRoomId = m.ChatRoomId,
                MessageType = (int)m.MessageType
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
                MessageType = (int)message.MessageType
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
                MessageType = (int)message.MessageType
            };
        }
    }
}