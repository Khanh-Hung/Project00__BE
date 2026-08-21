using System.Text.Json;
using Application.DTOs;

namespace Application.Common;

public static class RelationshipStageResolver
{
    public static (int Level, string StageName, string StageGuideline) Resolve(int affectionScore, string? customMilestonesJson = null)
    {
        var level = CalculateLevel(affectionScore);
        var name = GetDefaultLevelName(level);
        var guideline = GetDefaultLevelGuideline(level);

        if (!string.IsNullOrWhiteSpace(customMilestonesJson))
        {
            try
            {
                var milestones = JsonSerializer.Deserialize<List<RelationshipMilestoneDto>>(customMilestonesJson);
                var matched = milestones?.FirstOrDefault(ms => affectionScore >= ms.MinScore && affectionScore <= ms.MaxScore);
                if (matched != null)
                {
                    name = matched.Name;
                    guideline = matched.Description;
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        return (level, name, guideline);
    }

    public static int CalculateLevel(int score) => score switch
    {
        <= -76 => -4, // Kẻ Thù Truyền Kiếp (Nemesis)
        <= -51 => -3, // Căm Ghét & Khinh Bỉ (Hatred)
        <= -26 => -2, // Ác Cảm & Đề Phòng (Hostile)
        <= 0 => 1,    // Người Lạ (Stranger)
        <= 25 => 2,   // Người Quen & Cởi Mở (Acquaintance)
        <= 50 => 3,   // Bạn Thân Thiết (Close Friend)
        <= 75 => 4,   // Tri Kỷ & Rung Động (Soulmate)
        _ => 5        // Gắn Kết Linh Hồn (Eternal Devotion)
    };

    public static string GetDefaultLevelName(int level) => level switch
    {
        -4 => "Kẻ Thù Truyền Kiếp",
        -3 => "Căm Ghét & Khinh Bỉ",
        -2 => "Ác Cảm & Đề Phòng",
        1 => "Người Lạ",
        2 => "Người Quen & Cởi Mở",
        3 => "Bạn Thân Thiết",
        4 => "Tri Kỷ & Rung Động",
        _ => "Gắn Kết Linh Hồn"
    };

    public static string GetDefaultLevelGuideline(int level) => level switch
    {
        -4 => "Nhân vật cực kỳ căm ghét bạn, dùng lời lẽ cay độc, đe dọa, khinh bỉ, sẵn sàng tấn công hoặc tìm cách trừng phạt bạn.",
        -3 => "Nhân vật khó chịu, mỉa mai cay độc, cự tuyệt tiếp xúc và giữ khoảng cách tối đa.",
        -2 => "Nhân vật lạnh nhạt, xa cách, hoài nghi mọi hành động và không tin lời bạn.",
        1 => "Nhân vật giữ khoảng cách lịch thiệp xã giao, thận trọng quan sát, chưa mở lòng.",
        2 => "Nhân vật bắt đầu cởi mở, thoải mái trò chuyện và sẵn sàng chia sẻ thói quen đời thường.",
        3 => "Nhân vật tin tưởng, xưng hô gần gũi, thích trêu đùa và sẵn sàng giúp đỡ bạn.",
        4 => "Nhân vật gắn kết sâu sắc, ưu tiên bạn hàng đầu, tin tưởng chia sẻ những bí mật thầm kín.",
        _ => "Nhân vật dành trọn trái tim, nguyện hy sinh và tuyệt đối chung thủy bên bạn trọn đời."
    };
}
