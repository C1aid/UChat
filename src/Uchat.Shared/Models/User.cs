using System.Collections.Generic;

namespace Uchat.Shared.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProfileInfo { get; set; } = string.Empty;
        public string Theme { get; set; } = "Latte";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastSeen { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public byte[]? Avatar { get; set; }

        public List<Message> Messages { get; set; } = new();
        public List<ChatRoomMember> ChatRooms { get; set; } = new();
    }
}