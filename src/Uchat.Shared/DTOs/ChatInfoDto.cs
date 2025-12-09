using System;

namespace Uchat.Shared.DTOs
{
    public class ChatInfoDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsGroup { get; set; }
        public string? Description { get; set; }
        public int OtherUserId { get; set; }
        public string OtherUsername { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int UnreadCount { get; set; }
        public string? LastMessage { get; set; }
        public DateTime? LastMessageTime { get; set; }
        public byte[]? Avatar { get; set; }
    }
}