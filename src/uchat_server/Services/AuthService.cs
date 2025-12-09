using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uchat.Shared.DTOs;
using Uchat.Shared.Models;
using uchat_server.Data;

namespace uchat_server.Services
{
    public class AuthService
    {
        private readonly ChatContext _context;
        private readonly ILogger<AuthService> _logger;

        public AuthService(ChatContext context, ILogger<AuthService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ApiResponse> RegisterAsync(string username, string password, string displayName)
        {
            try
            {
                _logger.LogInformation("Starting registration process for username: {Username}", username);

                bool userExists = await _context.Users.AnyAsync(u => u.Username == username);
                _logger.LogInformation("User existence check for {Username}: {Exists}", username, userExists);

                if (userExists)
                {
                    _logger.LogWarning("Registration failed - username already exists: {Username}", username);
                    return new ApiResponse { Success = false, Message = "Username already exists" };
                }

                // Убеждаемся, что все обязательные поля заполнены
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = username;
                }

                var user = new User
                {
                    Username = username,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    DisplayName = displayName,
                    ProfileInfo = "", // Обязательное поле должно быть заполнено
                    Theme = "Latte", // Устанавливаем значение по умолчанию
                    CreatedAt = DateTime.UtcNow
                };

                _logger.LogInformation("Adding user to database context: {Username}", username);
                _context.Users.Add(user);

                _logger.LogInformation("Saving user to database: {Username}", username);
                int saved = await _context.SaveChangesAsync();
                _logger.LogInformation("Database save completed. Rows affected: {RowsAffected}, User ID: {UserId}",
                    saved, user.Id);

                _logger.LogInformation("User registered successfully: {Username} with ID: {UserId}", username, user.Id);

                // Возвращаем UserProfileDto для совместимости с клиентом
                var userProfileDto = new UserProfileDto
                {
                    Id = user.Id,
                    Username = user.Username ?? string.Empty,
                    DisplayName = user.DisplayName ?? string.Empty,
                    ProfileInfo = user.ProfileInfo ?? string.Empty,
                    Theme = user.Theme ?? "Latte",
                    Avatar = user.Avatar,
                };

                return new ApiResponse
                {
                    Success = true,
                    Message = "User registered successfully",
                    Data = userProfileDto
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed for username: {Username}. Error: {ErrorMessage}",
                    username, ex.Message);
                return new ApiResponse { Success = false, Message = "Registration failed" };
            }
        }

        public async Task<ApiResponse> LoginAsync(string username, string password)
        {
            try
            {
                _logger.LogInformation("Starting login process for username: {Username}", username);

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
                if (user == null)
                {
                    _logger.LogWarning("Login failed - user not found: {Username}", username);
                    return new ApiResponse { Success = false, Message = "Invalid username or password" };
                }

                _logger.LogInformation("User found for login: {Username} (ID: {UserId})", username, user.Id);

                bool isPasswordValid = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
                if (!isPasswordValid)
                {
                    _logger.LogWarning("Login failed - invalid password for user: {Username}", username);
                    return new ApiResponse { Success = false, Message = "Invalid username or password" };
                }

                _logger.LogInformation("Password verified successfully for user: {Username}", username);

                _logger.LogInformation("Login successful for user: {Username}", username);

                // Возвращаем UserProfileDto для совместимости с клиентом
                // Обрабатываем null значения для безопасности
                var userProfileDto = new UserProfileDto
                {
                    Id = user.Id,
                    Username = user.Username ?? string.Empty,
                    DisplayName = user.DisplayName ?? string.Empty,
                    ProfileInfo = user.ProfileInfo ?? string.Empty,
                    Theme = user.Theme ?? "Latte",
                    Avatar = user.Avatar,
                };
                
                // Проверяем, что Username не пустой (критично для работы)
                if (string.IsNullOrEmpty(userProfileDto.Username))
                {
                    _logger.LogError("User {UserId} has empty Username in database!", user.Id);
                    // Пытаемся восстановить из других полей или используем ID
                    userProfileDto.Username = $"user_{user.Id}";
                }
                
                _logger.LogInformation("Returning user data: Id={Id}, Username={Username}, DisplayName={DisplayName}, Theme={Theme}, Avatar={HasAvatar}", 
                    userProfileDto.Id, userProfileDto.Username, userProfileDto.DisplayName, userProfileDto.Theme, userProfileDto.Avatar != null);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Login successful",
                    Data = userProfileDto
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed for username: {Username}. Error: {ErrorMessage}",
                    username, ex.Message);
                return new ApiResponse { Success = false, Message = "Login failed" };
            }
        }

        public async Task<User?> GetUserByUsernameAsync(string username)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        }

        // НОВЫЕ МЕТОДЫ (адаптированные под существующие DTO)
        public async Task<ApiResponse> UpdateUserProfileAsync(int userId, UpdateProfileRequest request)
        {
            try
            {
                _logger.LogInformation("Updating profile for user ID: {UserId}", userId);
                _logger.LogInformation("Request data: Username={Username}, DisplayName={DisplayName}, ProfileInfo length={ProfileInfoLength}, Theme={Theme}, Avatar={HasAvatar}",
                    request.Username ?? "null",
                    request.DisplayName ?? "null",
                    request.ProfileInfo?.Length ?? 0,
                    request.Theme ?? "null",
                    request.Avatar != null);

                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    _logger.LogWarning("User not found for profile update: {UserId}", userId);
                    return new ApiResponse { Success = false, Message = "User not found" };
                }

                bool hasChanges = false;

                // Обновляем только предоставленные поля
                if (!string.IsNullOrEmpty(request.Username) && request.Username != user.Username)
                {
                    // Проверяем, не занят ли новый username
                    bool usernameExists = await _context.Users.AnyAsync(u => u.Username == request.Username && u.Id != userId);
                    if (usernameExists)
                    {
                        _logger.LogWarning("Username already exists: {Username}", request.Username);
                        return new ApiResponse { Success = false, Message = "Username already exists" };
                    }
                    user.Username = request.Username;
                    hasChanges = true;
                    _logger.LogInformation("Updated Username to: {Username}", request.Username);
                }

                if (!string.IsNullOrEmpty(request.DisplayName) && request.DisplayName != user.DisplayName)
                {
                    user.DisplayName = request.DisplayName;
                    hasChanges = true;
                    _logger.LogInformation("Updated DisplayName to: {DisplayName}", request.DisplayName);
                }

                // ProfileInfo может быть пустым, поэтому обновляем всегда, если поле предоставлено
                // (проверяем, что это не null, чтобы отличить от случая, когда поле не было отправлено)
                if (request.ProfileInfo != null && request.ProfileInfo != user.ProfileInfo)
                {
                    user.ProfileInfo = request.ProfileInfo;
                    hasChanges = true;
                    _logger.LogInformation("Updated ProfileInfo (length: {Length})", request.ProfileInfo.Length);
                }

                if (!string.IsNullOrEmpty(request.Theme) && request.Theme != user.Theme)
                {
                    user.Theme = request.Theme;
                    hasChanges = true;
                    _logger.LogInformation("Updated Theme to: {Theme}", request.Theme);
                }

                // Avatar обновляем только если предоставлен новый (не null)
                if (request.Avatar != null)
                {
                    user.Avatar = request.Avatar;
                    hasChanges = true;
                    _logger.LogInformation("Updated Avatar (length: {Length})", request.Avatar.Length);
                }

                if (hasChanges)
                {
                    user.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Profile updated successfully for user: {UserId}", userId);
                }
                else
                {
                    _logger.LogInformation("No changes detected for user: {UserId}", userId);
                }

                // Возвращаем UserProfileDto для совместимости
                var userProfileDto = new UserProfileDto
                {
                    Id = user.Id,
                    Username = user.Username ?? string.Empty,
                    DisplayName = user.DisplayName ?? string.Empty,
                    ProfileInfo = user.ProfileInfo ?? string.Empty,
                    Theme = user.Theme ?? "Latte",
                    Avatar = user.Avatar,
                };

                return new ApiResponse
                {
                    Success = true,
                    Message = "Profile updated successfully",
                    Data = userProfileDto
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating profile for user ID: {UserId}", userId);
                return new ApiResponse { Success = false, Message = "Error updating profile" };
            }
        }

        public async Task<ApiResponse> DeleteUserAccountAsync(int userId)
        {
            try
            {
                _logger.LogInformation("Deleting account for user ID: {UserId}", userId);

                // Проверяем, что userId валидный
                if (userId <= 0)
                {
                    _logger.LogWarning("Invalid userId for deletion: {UserId}", userId);
                    return new ApiResponse { Success = false, Message = "Invalid user ID" };
                }

                // Получаем пользователя для проверки (используем AsNoTracking для чтения)
                var user = await _context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (user == null)
                {
                    _logger.LogWarning("User not found for deletion: {UserId}", userId);
                    return new ApiResponse { Success = false, Message = "User not found" };
                }

                _logger.LogInformation("Found user to delete: Id={UserId}, Username={Username}", userId, user.Username);

                // Подсчитываем количество сообщений пользователя перед удалением
                var messageCount = await _context.Messages
                    .Where(m => m.UserId == userId)
                    .CountAsync();
                _logger.LogInformation("Deleting {MessageCount} messages for user {UserId}", messageCount, userId);

                // Удаляем все сообщения пользователя напрямую через запрос
                var userMessages = await _context.Messages
                    .Where(m => m.UserId == userId)
                    .ToListAsync();
                
                if (userMessages.Any())
                {
                    _context.Messages.RemoveRange(userMessages);
                }

                // Подсчитываем количество членств в чатах
                var membershipCount = await _context.ChatRoomMembers
                    .Where(crm => crm.UserId == userId)
                    .CountAsync();
                _logger.LogInformation("Deleting {MembershipCount} chat memberships for user {UserId}", membershipCount, userId);

                // Удаляем пользователя из чатов напрямую через запрос
                var chatMemberships = await _context.ChatRoomMembers
                    .Where(crm => crm.UserId == userId)
                    .ToListAsync();
                
                if (chatMemberships.Any())
                {
                    _context.ChatRoomMembers.RemoveRange(chatMemberships);
                }

                // Проверяем количество пользователей перед удалением
                var totalUsersBefore = await _context.Users.CountAsync();
                _logger.LogInformation("Total users in database before deletion: {TotalUsers}", totalUsersBefore);

                // Удаляем самого пользователя (загружаем заново для отслеживания)
                var userToDelete = await _context.Users.FindAsync(userId);
                if (userToDelete != null)
                {
                    _context.Users.Remove(userToDelete);
                }

                await _context.SaveChangesAsync();

                // Проверяем количество пользователей после удаления
                var totalUsersAfter = await _context.Users.CountAsync();
                _logger.LogInformation("Total users in database after deletion: {TotalUsers}", totalUsersAfter);
                _logger.LogInformation("Account deleted successfully for user: {UserId} (Username: {Username}). Deleted {MessageCount} messages and {MembershipCount} memberships.", 
                    userId, user.Username, messageCount, membershipCount);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Account deleted successfully"
                };
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "Concurrency exception when deleting account for user ID: {UserId}. User may have already been deleted.", userId);
                // Проверяем, действительно ли пользователь был удален
                var stillExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!stillExists)
                {
                    _logger.LogInformation("User {UserId} was successfully deleted (verified after concurrency exception)", userId);
                    return new ApiResponse
                    {
                        Success = true,
                        Message = "Account deleted successfully"
                    };
                }
                return new ApiResponse { Success = false, Message = "Error deleting account: concurrency conflict" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting account for user ID: {UserId}", userId);
                return new ApiResponse { Success = false, Message = "Error deleting account" };
            }
        }

        public async Task<ApiResponse> GetUserProfileAsync(int userId)
        {
            try
            {
                _logger.LogInformation("Getting profile for user ID: {UserId}", userId);

                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    _logger.LogWarning("User not found for profile retrieval: {UserId}", userId);
                    return new ApiResponse { Success = false, Message = "User not found" };
                }

                var userProfileDto = new UserProfileDto
                {
                    Id = user.Id,
                    Username = user.Username ?? string.Empty,
                    DisplayName = user.DisplayName ?? string.Empty,
                    ProfileInfo = user.ProfileInfo ?? string.Empty,
                    Theme = user.Theme ?? "Latte",
                    Avatar = user.Avatar,
                };

                _logger.LogInformation("Profile retrieved successfully for user: {UserId}", userId);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Profile retrieved successfully",
                    Data = userProfileDto
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting profile for user ID: {UserId}", userId);
                return new ApiResponse { Success = false, Message = "Error getting profile" };
            }
        }
    }
}