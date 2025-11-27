using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using uchat_server.Data;

namespace uchat_server.Data
{
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChatContext>
    {
        public ChatContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ChatContext>();
            optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=uchat;Username=postgres;Password=securepass");

            return new ChatContext(optionsBuilder.Options);
        }
    }
}