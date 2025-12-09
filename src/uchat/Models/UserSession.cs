using Uchat.Shared.DTOs;

namespace uchat.Models
{
    public class UserSession
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProfileInfo { get; set; } = string.Empty;
        public string Theme { get; set; } = "Latte";
        public byte[]? Avatar { get; set; }

        public static UserSession Current { get; set; } = new UserSession();

        public void UpdateFromUserProfileDto(UserProfileDto dto)
        {
            UserId = dto.Id;
            Username = dto.Username;
            DisplayName = dto.DisplayName;
            ProfileInfo = dto.ProfileInfo;
            Theme = dto.Theme;
            Avatar = dto.Avatar;
        }
    }
}