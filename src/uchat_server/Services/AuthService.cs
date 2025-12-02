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

        public async Task<ApiResponse> RegisterAsync(string username, string password, string firstName, string? lastName = null)
        {
            try
            {
                _logger.LogInformation("Starting registration process for username: {Username}", username);

                // Проверка существования пользователя
                bool userExists = await _context.Users.AnyAsync(u => u.Username == username);
                _logger.LogInformation("User existence check for {Username}: {Exists}", username, userExists);

                if (userExists)
                {
                    _logger.LogWarning("Registration failed - username already exists: {Username}", username);
                    return new ApiResponse { Success = false, Message = "Username already exists" };
                }

                // Создание пользователя
                var user = new User
                {
                    Username = username,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    FirstName = firstName ?? "",
                    LastName = lastName ?? "",
                    CreatedAt = DateTime.UtcNow
                };

                _logger.LogInformation("Adding user to database context: {Username}", username);
                _context.Users.Add(user);

                _logger.LogInformation("Saving user to database: {Username}", username);
                int saved = await _context.SaveChangesAsync();
                _logger.LogInformation("Database save completed. Rows affected: {RowsAffected}, User ID: {UserId}",
                    saved, user.Id);

                _logger.LogInformation("User registered successfully: {Username} with ID: {UserId}", username, user.Id);

                return new ApiResponse
                {
                    Success = true,
                    Message = "User registered successfully",
                    Data = new
                    {
                        UserId = user.Id,
                        Username = user.Username
                    }
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

                user.LastSeen = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Login successful for user: {Username}", username);

                var userData = new { UserId = user.Id, Username = user.Username };
                _logger.LogInformation("Returning user data: {UserData}", userData);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Login successful",
                    Data = userData
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
    }
}