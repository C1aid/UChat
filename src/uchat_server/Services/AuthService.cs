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

        public async Task<ApiResponse> RegisterAsync(string username, string password)
        {
            try
            {
                if (await _context.Users.AnyAsync(u => u.Username == username))
                {
                    return new ApiResponse { Success = false, Message = "Username already exists" };
                }

                var user = new User
                {
                    Username = username,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                return new ApiResponse
                {
                    Success = true,
                    Message = "User registered successfully",
                    Data = new { UserId = user.Id, Username = user.Username }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during user registration");
                return new ApiResponse { Success = false, Message = "Registration failed" };
            }
        }

        public async Task<ApiResponse> LoginAsync(string username, string password)
        {
            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
                if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                {
                    return new ApiResponse { Success = false, Message = "Invalid username or password" };
                }

                user.LastSeen = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return new ApiResponse
                {
                    Success = true,
                    Message = "Login successful",
                    Data = new { UserId = user.Id, Username = user.Username }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during user login");
                return new ApiResponse { Success = false, Message = "Login failed" };
            }
        }

        public async Task<User?> GetUserByUsernameAsync(string username)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        }
    }
}