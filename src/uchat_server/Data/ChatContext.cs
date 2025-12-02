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
        public DbSet<ChatRoomMember> ChatRoomMembers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            if (!options.IsConfigured)
            {
                options.UseNpgsql("Host=localhost;Port=5432;Database=uchat;Username=postgres;Password=securepass");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<Message>().ToTable("Messages");
            modelBuilder.Entity<ChatRoom>().ToTable("ChatRooms");
            modelBuilder.Entity<ChatRoomMember>().ToTable("ChatRoomMembers");

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.HasIndex(u => u.Username).IsUnique();
                entity.Property(u => u.Username).IsRequired().HasMaxLength(50);
                entity.Property(u => u.PasswordHash).IsRequired();
                entity.Property(u => u.FirstName).HasMaxLength(50);
                entity.Property(u => u.LastName).HasMaxLength(50);
                entity.Property(u => u.CreatedAt).IsRequired();
                entity.Property(u => u.LastSeen).IsRequired(false);
                entity.Property(u => u.Avatar).HasColumnType("bytea").IsRequired(false);
            });

            modelBuilder.Entity<Message>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Content).IsRequired();
                entity.Property(m => m.SentAt).IsRequired();

                entity.HasOne(m => m.User)
                    .WithMany(u => u.Messages)
                    .HasForeignKey(m => m.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(m => m.ChatRoom)
                    .WithMany(r => r.Messages)
                    .HasForeignKey(m => m.ChatRoomId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ChatRoom>(entity =>
            {
                entity.HasKey(r => r.Id);
                entity.Property(r => r.Name).IsRequired().HasMaxLength(100);
                entity.Property(r => r.IsGroup).IsRequired();
                entity.Property(r => r.Description).HasMaxLength(500);
                entity.Property(r => r.CreatedAt).IsRequired();
            });

            modelBuilder.Entity<ChatRoomMember>(entity =>
            {
                entity.HasKey(crm => new { crm.ChatRoomId, crm.UserId });

                entity.HasOne(crm => crm.ChatRoom)
                    .WithMany(r => r.Members)
                    .HasForeignKey(crm => crm.ChatRoomId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(crm => crm.User)
                    .WithMany(u => u.ChatRooms)
                    .HasForeignKey(crm => crm.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}