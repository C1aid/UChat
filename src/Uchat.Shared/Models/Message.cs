using System;
using Uchat.Shared.Enums;

namespace Uchat.Shared.Models
{
    public class Message
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public int UserId { get; set; }
        public User? User { get; set; }
        public int ChatRoomId { get; set; }
        public ChatRoom? ChatRoom { get; set; }
        public MessageType MessageType { get; set; } 
    }
}