namespace Domain.Enums;

public enum SessionStatus
{
    Active = 1,    // Phiên trò chuyện đang hoạt động bình thường
    WalkedOut = 2, // Nhân vật đã giận dữ bỏ đi / cắt đứt liên lạc (Rage Quit / Walk Out)
    Closed = 3     // Phiên trò chuyện đã được đóng chủ động bởi người dùng
}
