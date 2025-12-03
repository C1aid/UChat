using System;
using Uchat.Shared.Enums;

namespace Uchat.Shared.DTOs
{
    public class MessageDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int ChatRoomId { get; set; }
        public MessageType MessageType { get; set; } 
    }
}