using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uchat.Shared.DTOs;
using Uchat.Shared.Enums;
using Uchat.Shared.Models;
using uchat_server.Data;

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

        public async Task<ApiResponse> SendMessageAsync(Message message)
        {
            try
            {
                _context.Messages.Add(message);
                await _context.SaveChangesAsync();

                return new ApiResponse
                {
                    Success = true,
                    Message = "Message sent",
                    Data = new { MessageId = message.Id }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message");
                return new ApiResponse { Success = false, Message = "Failed to send message" };
            }
        }

        public async Task<List<MessageDto>> GetRecentMessagesAsync(int count = 10)
        {
            return await _context.Messages
                .Include(m => m.User)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .Select(m => new MessageDto
                {
                    Id = m.Id,
                    Content = m.Content,
                    Username = m.User.Username,
                    SentAt = m.SentAt,
                    MessageType = m.MessageType
                })
                .ToListAsync();
        }
        
        public async Task<List<int>> GetRoomUserIdsAsync(int roomId)
        {
            return await _context.Set<ChatRoomMember>()
                .Where(m => m.ChatRoomId == roomId)
                .Select(m => m.UserId)
                .ToListAsync();
        }

        public async Task<bool> IsUserInRoomAsync(int userId, int roomId)
        {
            return await _context.Set<ChatRoomMember>()
                .AnyAsync(m => m.UserId == userId && m.ChatRoomId == roomId);
        }


        public async Task<int> CreatePrivateChatAsync(int currentUserId, string targetUsername)
        {
            var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Username == targetUsername);
            if (targetUser == null) return -1;
            if (targetUser.Id == currentUserId) return -2;

            var existingChat = await _context.ChatRooms
                .Where(r => !r.IsGroup)
                .Where(r => r.Members.Any(m => m.UserId == currentUserId) && 
                            r.Members.Any(m => m.UserId == targetUser.Id))
                .FirstOrDefaultAsync();

            if (existingChat != null)
            {
                return existingChat.Id;
            }

            var newChat = new ChatRoom
            {
                IsGroup = false,
                Name = string.Empty,
                CreatedAt = DateTime.UtcNow
            };

            newChat.Members.Add(new ChatRoomMember { UserId = currentUserId, IsAdmin = true });
            newChat.Members.Add(new ChatRoomMember { UserId = targetUser.Id, IsAdmin = true });

            _context.ChatRooms.Add(newChat);
            await _context.SaveChangesAsync();

            return newChat.Id;
        }
    }
}