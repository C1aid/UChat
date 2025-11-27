using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using uchat_server.Data;

namespace uchat_server.Services
{
    public class DatabaseInitializer
    {
        private readonly ChatContext _context;
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(ChatContext context, ILogger<DatabaseInitializer> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            try
            {
                await _context.Database.MigrateAsync();
                _logger.LogInformation("Database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                throw;
            }
        }
    }
}