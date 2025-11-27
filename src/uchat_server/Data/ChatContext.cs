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

            // 2. Настройка Сообщения
            modelBuilder.Entity<Message>(entity =>
            {
                entity.HasOne(m => m.User)
                    .WithMany(u => u.Messages)
                    .HasForeignKey(m => m.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // Если удалить юзера, удалятся его сообщения

                entity.HasOne(m => m.ChatRoom)
                    .WithMany(r => r.Messages)
                    .HasForeignKey(m => m.ChatRoomId)
                    .OnDelete(DeleteBehavior.Cascade); // Если удалить чат, удалится переписка
            });

            // 3. Настройка Участников чата (Самое важное для групп!)
            modelBuilder.Entity<ChatRoomMember>(entity =>
            {
                // Составной ключ: (ChatRoomId + UserId)
                // Это гарантирует, что один человек не может вступить в одну группу дважды
                entity.HasKey(crm => new { crm.ChatRoomId, crm.UserId });

                // Связь: Участник -> Комната
                entity.HasOne(crm => crm.ChatRoom)
                    .WithMany(r => r.Members)
                    .HasForeignKey(crm => crm.ChatRoomId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Связь: Участник -> Юзер
                entity.HasOne(crm => crm.User)
                    .WithMany(u => u.ChatRooms) // Убедитесь, что добавили список ChatRooms в User.cs!
                    .HasForeignKey(crm => crm.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}