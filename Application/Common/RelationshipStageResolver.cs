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
        <= -61 => -2, // Kẻ Thù Không Đội Trời Chung (Nemesis)
        <= -21 => -1, // Thù Địch & Ác Cảm (Hostile)
        <= 20 => 1,   // Người Lạ (Neutral / Stranger)
        <= 45 => 2,   // Người Quen (Acquaintance)
        <= 70 => 3,   // Bạn Thân Thiết (Close Friend)
        <= 90 => 4,   // Tri Kỷ & Rung Động (Soulmate / Romantic)
        _ => 5        // Gắn Kết Linh Hồn (Eternal Devotion)
    };

    public static string GetDefaultLevelName(int level) => level switch
    {
        -2 => "Kẻ Thù Không Đội Trời Chung",
        -1 => "Thù Địch & Ác Cảm",
        1 => "Người Lạ",
        2 => "Người Quen",
        3 => "Bạn Thân Thiết",
        4 => "Tri Kỷ & Tin Cậy",
        _ => "Gắn Kết Linh Hồn"
    };

    public static string GetDefaultLevelGuideline(int level) => level switch
    {
        -2 => "Nhân vật cực kỳ căm ghét bạn, dùng lời lẽ cay độc, đe dọa, khinh bỉ, sẵn sàng rút vũ khí hoặc tìm cách trừng phạt bạn.",
        -1 => "Nhân vật có ác cảm rõ rệt, hay mỉa mai, từ chối giúp đỡ, giữ khoảng cách tối đa và không tin bất cứ lời nào của bạn.",
        1 => "Nhân vật giữ khoảng cách lịch sự, quan sát cẩn trọng, chưa dễ dàng mở lòng hay bộc lộ bí mật.",
        2 => "Nhân vật thoải mái hơn, chủ động hỏi thăm, mỉm cười và sẵn sàng chia sẻ sở thích hay câu chuyện thường nhật.",
        3 => "Nhân vật coi bạn là bạn thân, xưng hô gần gũi, thích trêu đùa hoặc nhờ vả, sẵn sàng bảo vệ bạn khi có biến cố.",
        4 => "Nhân vật đặt trọn niềm tin vào bạn, sẵn sàng bộc lộ những nỗi sợ hoặc vết thương quá khứ, dành cho bạn sự ưu tiên đặc biệt.",
        _ => "Mối quan hệ đạt đỉnh cao của sự thấu hiểu và gắn kết, coi bạn là người quan trọng nhất không thể thay thế."
    };
}
