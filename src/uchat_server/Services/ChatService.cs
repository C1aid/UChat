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
                .Where(m => !m.IsDeleted)
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
    }
}