using Microsoft.EntityFrameworkCore;
using Uchat.Shared.Models;

namespace uchat_server.Data
{
    public class ChatContext : DbContext
    {
        public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

        public ChatContext() { }

        public DbSet<User> Users { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<ChatRoom> ChatRooms { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            if (!options.IsConfigured)
            {
                options.UseNpgsql("Host=localhost;Port=5432;Database=uchat;Username=postgres;Password=securepass");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(u => u.Username).IsUnique();
            });

            modelBuilder.Entity<Message>(entity =>
            {
                entity.HasOne(m => m.User)
                      .WithMany(u => u.Messages)
                      .HasForeignKey(m => m.UserId);
            });
        }
    }
}