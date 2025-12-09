using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using uchat_server.Data;

namespace uchat_server.Services
{
    public class DatabaseCleanupService
    {
        private readonly ChatContext _context;
        private readonly ILogger<DatabaseCleanupService> _logger;

        public DatabaseCleanupService(ChatContext context, ILogger<DatabaseCleanupService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Очищает старые сообщения (старше указанного количества дней)
        /// </summary>
        public async Task<int> CleanupOldMessages(int daysToKeep = 90)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);
                var oldMessages = await _context.Messages
                    .Where(m => m.SentAt < cutoffDate)
                    .ToListAsync();

                if (oldMessages.Count > 0)
                {
                    _context.Messages.RemoveRange(oldMessages);
                    var deleted = await _context.SaveChangesAsync();
                    _logger.LogInformation("Cleaned up {Count} old messages (older than {Days} days)", deleted, daysToKeep);
                    return deleted;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old messages");
                return 0;
            }
        }

        /// <summary>
        /// Очищает пустые чаты (без сообщений и участников)
        /// </summary>
        public async Task<int> CleanupEmptyChatRooms()
        {
            try
            {
                // Получаем ID чатов, которые имеют сообщения
                var roomsWithMessages = await _context.Messages
                    .Select(m => m.ChatRoomId)
                    .Distinct()
                    .ToListAsync();

                // Получаем ID чатов, которые имеют участников
                var roomsWithMembers = await _context.ChatRoomMembers
                    .Select(crm => crm.ChatRoomId)
                    .Distinct()
                    .ToListAsync();

                // Находим чаты без сообщений и без участников
                var emptyRooms = await _context.ChatRooms
                    .Where(r => !roomsWithMessages.Contains(r.Id) && !roomsWithMembers.Contains(r.Id))
                    .ToListAsync();

                if (emptyRooms.Count > 0)
                {
                    _context.ChatRooms.RemoveRange(emptyRooms);
                    var deleted = await _context.SaveChangesAsync();
                    _logger.LogInformation("Cleaned up {Count} empty chat rooms", deleted);
                    return deleted;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up empty chat rooms");
                return 0;
            }
        }

        /// <summary>
        /// Исправляет некорректные данные пользователей (null значения)
        /// </summary>
        public async Task<int> FixInvalidUserData()
        {
            try
            {
                var usersWithIssues = await _context.Users
                    .Where(u => string.IsNullOrEmpty(u.Username) || 
                               string.IsNullOrEmpty(u.DisplayName) ||
                               string.IsNullOrEmpty(u.Theme))
                    .ToListAsync();

                int fixedCount = 0;
                foreach (var user in usersWithIssues)
                {
                    bool hasChanges = false;

                    if (string.IsNullOrEmpty(user.Username))
                    {
                        user.Username = $"user_{user.Id}";
                        hasChanges = true;
                        _logger.LogWarning("Fixed empty Username for user ID: {UserId}", user.Id);
                    }

                    if (string.IsNullOrEmpty(user.DisplayName))
                    {
                        user.DisplayName = user.Username;
                        hasChanges = true;
                    }

                    if (string.IsNullOrEmpty(user.Theme))
                    {
                        user.Theme = "Latte";
                        hasChanges = true;
                    }

                    if (hasChanges)
                    {
                        user.UpdatedAt = DateTime.UtcNow;
                        fixedCount++;
                    }
                }

                if (fixedCount > 0)
                {
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Fixed invalid data for {Count} users", fixedCount);
                }

                return fixedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fixing invalid user data");
                return 0;
            }
        }

        /// <summary>
        /// Выполняет полную очистку БД
        /// </summary>
        public async Task<CleanupResult> PerformFullCleanup(int messageRetentionDays = 90)
        {
            var result = new CleanupResult();

            try
            {
                _logger.LogInformation("Starting database cleanup...");

                // Исправляем некорректные данные пользователей
                result.FixedUsers = await FixInvalidUserData();

                // Очищаем старые сообщения
                result.DeletedMessages = await CleanupOldMessages(messageRetentionDays);

                // Очищаем пустые чаты
                result.DeletedChatRooms = await CleanupEmptyChatRooms();

                _logger.LogInformation("Database cleanup completed: Fixed {FixedUsers} users, Deleted {DeletedMessages} messages, Deleted {DeletedChatRooms} chat rooms",
                    result.FixedUsers, result.DeletedMessages, result.DeletedChatRooms);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during full database cleanup");
                return result;
            }
        }

        public class CleanupResult
        {
            public int FixedUsers { get; set; }
            public int DeletedMessages { get; set; }
            public int DeletedChatRooms { get; set; }
        }
    }
}

