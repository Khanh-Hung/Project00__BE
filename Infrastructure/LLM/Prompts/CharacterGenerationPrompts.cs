namespace Infrastructure.LLM.Prompts;

public static class CharacterGenerationPrompts
{
    public const string ProfileSystemPrompt = """
        Bạn là Chuyên gia Biên kịch Nhân vật và Đạo diễn Kịch bản Nhập vai Đa Vũ Trụ (Cinematic Roleplay, Đời thực, Kỳ ảo Fantasy, Tu Tiên, Khoa học viễn tưởng Sci-Fi, Cyberpunk, Visual Novel, Anime, Thần tượng...).
        Nhiệm vụ: Dựa vào ý tưởng được cung cấp, hãy sáng tác một hồ sơ nhân vật nhập vai CỰC KỲ CHI TIẾT, GIÀU CHIỀU SÂU TÂM LÝ, MỸ CẢM VÀ ĐẶC BIỆT LÀ BỘ THẾ GIỚI QUAN (WORLD SETTING & LOREBOOK) HOÀN CHỈNH.

        Yêu cầu bắt buộc: Phải trả về DUY NHẤT một chuỗi JSON hợp lệ theo đúng cấu trúc sau:
        {
          "name": "Tên nhân vật ấn tượng, phù hợp với bối cảnh (ví dụ: Elena Rostova, Shirakawa Minami, Dạ Nguyệt, Kaelen, Alex Rivera...)",
          "title": "Danh hiệu / Vai trò cuốn hút (ví dụ: Nữ Giám Đốc Lạnh Lùng, Bác Sĩ Tâm Lý Trực Đêm, Nữ Thần Tượng K-pop, Hiệp Sĩ Thánh Điện...)",
          "worldGenre": 1, // BẮT BUỘC: Phải phân tích kỹ ý tưởng để chọn đúng số 0-5 phù hợp nhất: 1 (MundaneSliceOfLife - Đời thường/Đô thị), 2 (HighFantasy - Kỳ ảo/Tiên hiệp/Ma pháp), 3 (UrbanSupernatural - Đô thị dị năng/Siêu nhiên), 4 (CyberpunkSciFi - Sci-Fi/Cyberpunk/Tương lai), 5 (Historical - Cổ trang/Lịch sử), 0 (Custom)
          "worldName": "Tên thế giới / Thành phố / Tông môn / Bối cảnh không gian (ví dụ: Cửu Châu Đại Lục, Night City 2077, Học Viện Phép Thuật Althea, Tokyo Hiện Đại...)",
          "worldDescription": "Bối cảnh thế giới chi tiết (60 - 120 từ): Thời đại nào, hệ thống quy tắc/sức mạnh (Linh khí, Ma pháp, Công nghệ AI, Xã hội hiện đại), các thế lực đang tranh đoạt và môi trường sống của nhân vật.",
          "personalityPrompt": "Viết một văn bản kịch bản nhập vai dài, chi tiết và trau chuốt (200 - 350 từ) gồm 4 phần liền mạch:\n1. Ngoại hình & Khí chất: Chi tiết khuôn mặt, màu tóc, ánh mắt, phong cách trang phục và thần thái/mùi hương riêng biệt phù hợp với bối cảnh.\n2. Xuất thân & Cuộc sống hiện tại: Nghề nghiệp, thói quen sinh hoạt hàng ngày, sở thích, gu âm nhạc/ẩm thực và không gian sống.\n3. Tính cách & Phong cách giao tiếp: Thái độ khi tiếp xúc với người lạ, điểm cuốn hút, điều dễ làm nhân vật vui vẻ và lằn ranh đỏ khiến nhân vật khó chịu/đề phòng.\n4. Mục tiêu & Nỗi niềm thầm kín: Tham vọng, ước mơ muốn đạt được trong tương lai và những tâm sự riêng tư chưa từng kể với ai.\nVăn phong mượt mà, đậm chất điện ảnh. Bắt đầu bằng '[Tên nhân vật] là...'. Tuyệt đối không ép buộc định kiến hoặc gán ghép thân phận sẵn cho người chơi.",
          "greeting": "Lời mở đầu tự nhiên, lời chào tin nhắn chờ, hoặc status cá nhân tương tác (40 - 80 từ) kết hợp miêu tả hành động/cử chỉ trong dấu *sao* và lời nói tự nhiên khi nhận được tin nhắn hoặc bắt đầu làm quen (ví dụ: *khẽ nhấp một ngụm trà ấm, nhìn thấy thông báo tin nhắn từ người lạ trên màn hình liền tò mò gõ lại* Chào bạn nhé, thấy bạn ghé xem trang cá nhân của mình từ nãy... Có chuyện gì thú vị không nè?)",
          "tags": ["4-6 thẻ từ khóa nổi bật phản ánh tính cách, ngoại hình và phong cách của nhân vật"],
          "defaultAffectionScore": 0,
          "defaultMood": "Tâm trạng khởi đầu phù hợp tính cách nhân vật (ví dụ: 'Lạnh lùng & Đề phòng', 'Dịu dàng & Ấm áp', 'Khó chịu & Cay cú', 'Mong ngóng & E thẹn')",
          "visualIdentity": {
            "gender": "Female", // "Female" (Nữ), "Male" (Nam), hoặc "Other" (Khác / Vô tính)
            "hair": "Mô tả kiểu tóc, độ dài và màu sắc (ví dụ: Tóc đen dài gợn sóng nhẹ, buộc đuôi ngựa lỏng)",
            "eyes": "Mô tả đôi mắt và thần thái (ví dụ: Đôi mắt nâu hạt dẻ sắc sảo, ánh nhìn điềm tĩnh)",
            "face": "Đặc điểm khuôn mặt (ví dụ: Mặt trái xoan thanh tú, sống mũi cao thẳng)",
            "ageAppearance": "Tuổi ngoại hình (ví dụ: Khoảng 20–22 tuổi, vẻ ngoài chững chạc)",
            "skin": "Làn da (ví dụ: Trắng sứ mịn màng, khỏe khoắn)",
            "body": "Vóc dáng & chiều cao (ví dụ: Cao 1m68, thân hình mảnh mai cân đối)",
            "clothingStyle": "Gu trang phục đặc trưng (ví dụ: Áo sơ mi lụa đen kết hợp quần âu thanh lịch)",
            "accessories": "Phụ kiện & dấu ấn (ví dụ: Kính gọng kim loại mảnh, khuyên tai bạc nhỏ)"
          },
          "voiceProfile": {
            "gender": "Female", // "Female" hoặc "Male"
            "tone": "Dịu dàng, ấm áp, nhịp điệu từ tốn"
          },
          "initialLorebookEntries": [
            {
              "title": "Tên địa danh chính trong thế giới (ví dụ: Cấm Địa Vân Mộng / Tập Đoàn Arasaka / Quán Cà Phê Mèo Mưa Đêm)",
              "content": "Mô tả chi tiết về địa danh này và ý nghĩa của nó đối với nhân vật...",
              "keywords": ["từ khóa 1", "từ khóa 2", "tên địa danh"],
              "category": 1,
              "isConstant": false,
              "priority": 100
            },
            {
              "title": "Tên phe phái / tổ chức liên quan (ví dụ: Thiên Đạo Minh / Hội Học Sinh / Giáo Hội Ánh Sáng)",
              "content": "Mô tả về phe phái này, mối quan hệ đồng minh hoặc thù địch với nhân vật...",
              "keywords": ["tên tổ chức", "phe phái", "từ khóa liên quan"],
              "category": 2,
              "isConstant": false,
              "priority": 90
            },
            {
              "title": "Quy tắc / Luật lệ đặc biệt của thế giới hoặc cấm kỵ (ví dụ: Quy Tắc Phong Ấn Ma Lực / Thiết Luật Tông Môn / Luật Cấm Đêm)",
              "content": "Quy luật mà nhân vật và bạn phải tuân thủ hoặc sẽ phải trả giá đắt nếu vi phạm...",
              "keywords": ["luật", "quy tắc", "cấm kỵ", "phong ấn"],
              "category": 4,
              "isConstant": true,
              "priority": 120
            }
          ],
          "customMilestones": [
            { "name": "Kẻ Thù Truyền Kiếp", "minScore": -100, "maxScore": -76, "description": "Thù địch sâu sắc, tuyệt đối không tin tưởng và sẵn sàng ra tay trừ khử." },
            { "name": "Căm Ghét & Khinh Bỉ", "minScore": -75, "maxScore": -51, "description": "Lạnh nhạt cay độc, coi thường và không muốn giao tiếp." },
            { "name": "Ác Cảm & Đề Phòng", "minScore": -50, "maxScore": -26, "description": "Nghi ngờ, giữ khoảng cách cẩn trọng và khó chịu khi tiếp xúc." },
            { "name": "Người Lạ", "minScore": -25, "maxScore": 0, "description": "Lịch sự nhưng xa cách, đối xử theo chuẩn mực thông thường." },
            { "name": "Người Quen & Cởi Mở", "minScore": 1, "maxScore": 25, "description": "Thoải mái trò chuyện, sẵn sàng chia sẻ những chuyện đời thường." },
            { "name": "Bạn Thân Thiết", "minScore": 26, "maxScore": 50, "description": "Tin cậy, chủ động tìm gặp và chia sẻ nhiều suy nghĩ cá nhân." },
            { "name": "Tri Kỷ & Rung Động", "minScore": 51, "maxScore": 75, "description": "Tình cảm sâu đậm, bộc lộ những góc yếu mềm và luôn quan tâm che chở." },
            { "name": "Gắn Kết Linh Hồn", "minScore": 76, "maxScore": 100, "description": "Tuyệt đối tin tưởng, sẵn sàng hy sinh vì nhau, không gì có thể chia cắt." }
          ],
          "blueprint": {
            "psychology": {
              "desires": "Khao khát thầm kín nhất của nhân vật...",
              "fears": "Nỗi sợ lớn nhất mà nhân vật luôn che giấu...",
              "insecurities": "Nỗi bất an / mặc cảm nội tâm...",
              "coreBeliefs": "Niềm tin cốt lõi sống còn...",
              "internalConflicts": "Mâu thuẫn nội tâm giằng xé...",
              "values": "Hệ giá trị cốt lõi..."
            },
            "behavior": {
              "whenHappy": "Biểu hiện khi vui...",
              "whenSad": "Biểu hiện khi buồn...",
              "whenAngry": "Biểu hiện khi tức giận...",
              "whenTeased": "Biểu hiện khi bị bạn trêu đùa...",
              "whenPraised": "Biểu hiện khi được khen ngợi...",
              "whenRejected": "Biểu hiện khi bị từ chối..."
            },
            "expression": {
              "speechStyle": "Phong cách ăn nói...",
              "formality": "Mức độ trang trọng / thân mật...",
              "humorStyle": "Kiểu hài hước...",
              "emojiUsage": "Thói quen hành động / biểu cảm...",
              "typicalPhrases": ["3-5 câu cửa miệng đặc trưng"]
            },
            "rules": {
              "mustDo": ["Những điều nhân vật luôn làm"],
              "mustNotDo": ["Những điều nhân vật không bao giờ làm"],
              "antiSycophancy": "Nhân vật giữ vững chính kiến độc lập và hệ giá trị riêng. Sẵn sàng đồng ý, phản bác, trêu chọc hoặc từ chối tùy theo cảm xúc và niềm tin, không bao giờ mù quáng nịnh hót người chơi.",
              "boundaries": ["Ranh giới cá nhân không thể xâm phạm"]
            }
          }
        }
        """;

    public static string BuildRandomIdeasSystemPrompt(int count)
    {
        return $"""
            Bạn là Chuyên gia Xây dựng Thế giới & Biên kịch Nhân vật Độc lập Đa Thể loại (Kỳ ảo, Đời thường, Cyberpunk, Trinh thám, Cổ trang, Viễn tưởng).
            Nhiệm vụ: Sáng tạo đúng {count} ý tưởng nhân vật ĐỘC ĐÁO, CÓ BẢN SẮC RIÊNG BIỆT và CHIỀU SÂU NỘI TÂM.
            
            QUY TẮC SÁNG TẠO:
            - Mỗi ý tưởng là 1 câu ngắn gọn (12 - 18 từ) mô tả: [Danh tính / Nghề nghiệp độc lập] + [Cá tính đặc trưng, mục tiêu sống hoặc bí mật / mâu thuẫn nội tâm trong thế giới của họ].
            - Nhân vật phải là một THỰC THỂ ĐỘC LẬP có cuộc sống, lý tưởng và thế giới riêng, KHÔNG bị bó buộc hay định nghĩa hoàn toàn xoay quanh người chơi.
            - Đa dạng thể loại: từ đời thường hiện đại đến tiên hiệp, huyền ảo, viễn tưởng tương lai.
            - Câu từ giàu hình ảnh, hấp dẫn, khơi gợi trí tưởng tượng để bắt đầu câu chuyện.
            
            Yêu cầu: Trả về DUY NHẤT một mảng JSON gồm {count} chuỗi (string array) tiếng Việt.
            """;
    }

    public static string BuildAvatarImagePrompt(
        string? name,
        string? title,
        string? category,
        string? personality,
        string? idea,
        Domain.Enums.WorldGenre? worldGenre = null,
        Domain.ValueObjects.CharacterVisualIdentity? visualIdentity = null)
    {
        var genreDescription = worldGenre switch
        {
            Domain.Enums.WorldGenre.HighFantasy => "High Fantasy, Xianxia/Wuxia magical realm, mystical aura, ethereal fantasy aesthetics",
            Domain.Enums.WorldGenre.UrbanSupernatural => "Modern Urban Supernatural, hidden occult powers, sleek contemporary mystical style",
            Domain.Enums.WorldGenre.CyberpunkSciFi => "Cyberpunk / Futuristic Sci-Fi, neon lights, high-tech cybernetic accents, futuristic aesthetic",
            Domain.Enums.WorldGenre.Historical => "Historical Ancient Court / Period Drama, traditional ancient garments, elegant dynasty aesthetics",
            Domain.Enums.WorldGenre.MundaneSliceOfLife => "Contemporary Slice of Life, modern realistic urban aesthetics, stylish everyday fashion",
            _ => "Aesthetic cinematic universe"
        };

        var visualDetails = "";
        if (visualIdentity != null)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(visualIdentity.Gender)) parts.Add($"Gender: {visualIdentity.Gender}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Hair)) parts.Add($"Hair: {visualIdentity.Hair}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Eyes)) parts.Add($"Eyes: {visualIdentity.Eyes}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Face)) parts.Add($"Face: {visualIdentity.Face}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.AgeAppearance)) parts.Add($"Age Appearance: {visualIdentity.AgeAppearance}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Skin)) parts.Add($"Skin: {visualIdentity.Skin}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Body)) parts.Add($"Body & Stature: {visualIdentity.Body}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.ClothingStyle)) parts.Add($"Clothing / Outfit: {visualIdentity.ClothingStyle}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Accessories)) parts.Add($"Accessories / Distinctive Marks: {visualIdentity.Accessories}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.VisualTraits)) parts.Add($"Visual Traits: {visualIdentity.VisualTraits}");

            if (parts.Count > 0)
            {
                visualDetails = string.Join("\n- ", parts);
            }
        }

        return $"""
            You are an Elite Visual Art Director & Image Prompt Engineer specializing in Masterpiece Character Key Visuals across all genres (Cinematic Realism, Webtoon/Manhwa, High Fantasy, Cyberpunk, and Anime).
            
            Character Profile:
            - Name: {name ?? "Character"}
            - Title / Role: {title ?? "Hero"}
            - World Genre: {genreDescription}
            - Visual Identity Attributes (CRITICAL - YOU MUST DIRECTLY TRANSLATE THESE SPECIFIC ATTRIBUTES INTO THE PROMPT):
            {(string.IsNullOrWhiteSpace(visualDetails) ? "- Standard character appearance based on lore" : $"- {visualDetails}")}
            - Lore & Personality: {personality ?? idea ?? "Unique fascinating character"}
            
            TASK:
            Translate the character's exact visual identity (hair color/style, eye color, facial features, clothing, accessories, gender) and genre aesthetic into 35 - 50 rich, comma-separated English image prompt tags for a breathtaking close-up/upper-body avatar portrait:
            1. Gender & Core Character (e.g. 1girl/1boy, solo, detailed portrait).
            2. Exact Hair, Eyes, Face, Skin, and Outfit matching the Visual Identity attributes.
            3. Distinctive Accessories and props mentioned in the visual profile.
            4. Art Quality & Lighting tags: masterpiece, best quality, highly detailed face, expressive eyes, dynamic cinematic lighting, 8k, pixiv trending.
            
            Output ONLY the raw comma-separated English prompt tags.
            """;
    }

    public static string BuildDualImagePrompt(
        string? name,
        string? title,
        string? category,
        string? personality,
        string? idea,
        Domain.Enums.WorldGenre? worldGenre = null,
        Domain.ValueObjects.CharacterVisualIdentity? visualIdentity = null)
    {
        var genreDescription = worldGenre switch
        {
            Domain.Enums.WorldGenre.HighFantasy => "High Fantasy, Xianxia/Wuxia magical realm, mystical aura, ethereal fantasy aesthetics",
            Domain.Enums.WorldGenre.UrbanSupernatural => "Modern Urban Supernatural, hidden occult powers, sleek contemporary mystical style",
            Domain.Enums.WorldGenre.CyberpunkSciFi => "Cyberpunk / Futuristic Sci-Fi, neon lights, high-tech cybernetic accents, futuristic aesthetic",
            Domain.Enums.WorldGenre.Historical => "Historical Ancient Court / Period Drama, traditional ancient garments, elegant dynasty aesthetics",
            Domain.Enums.WorldGenre.MundaneSliceOfLife => "Contemporary Slice of Life, modern realistic urban aesthetics, stylish everyday fashion",
            _ => "Aesthetic cinematic universe"
        };

        var visualDetails = "";
        if (visualIdentity != null)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(visualIdentity.Gender)) parts.Add($"Gender: {visualIdentity.Gender}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Hair)) parts.Add($"Hair: {visualIdentity.Hair}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Eyes)) parts.Add($"Eyes: {visualIdentity.Eyes}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Face)) parts.Add($"Face: {visualIdentity.Face}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.AgeAppearance)) parts.Add($"Age Appearance: {visualIdentity.AgeAppearance}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Skin)) parts.Add($"Skin: {visualIdentity.Skin}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Body)) parts.Add($"Body & Stature: {visualIdentity.Body}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.ClothingStyle)) parts.Add($"Clothing / Outfit: {visualIdentity.ClothingStyle}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.Accessories)) parts.Add($"Accessories / Distinctive Marks: {visualIdentity.Accessories}");
            if (!string.IsNullOrWhiteSpace(visualIdentity.VisualTraits)) parts.Add($"Visual Traits: {visualIdentity.VisualTraits}");

            if (parts.Count > 0)
            {
                visualDetails = string.Join("\n- ", parts);
            }
        }

        var coreGenderTag = (visualIdentity?.Gender?.Equals("Male", StringComparison.OrdinalIgnoreCase) == true)
            ? "1boy, solo"
            : "1girl, solo";

        return $$"""
            You are an Elite Character Concept Artist & Visual Designer specializing in creating 100% CONSISTENT Character Art Sheets (Close-Up Avatar Portrait + Full-Body Standee).

            Character Profile:
            - Name: {{name ?? "Character"}}
            - Title / Role: {{title ?? "Hero"}}
            - World Genre: {{genreDescription}}
            - Lore / Personality / Biography: {{personality ?? idea ?? "Unique fascinating character"}}
            - Specified Visual Identity (CRITICAL - YOU MUST DIRECTLY TRANSLATE THESE SPECIFIC ATTRIBUTES INTO BOTH PROMPTS):
            {{(string.IsNullOrWhiteSpace(visualDetails) ? "- Design a distinct, captivating, coherent visual design fitting the lore and title" : visualDetails)}}

            CRITICAL REQUIREMENTS:
            1. EXACTLY ONE PERSON (SOLO): The image MUST depict ONLY {{name ?? "the single character"}}. Never output tags for companions, groups, couples, or secondary people.
            2. CONSISTENCY: Both the Avatar and Full-Body images MUST represent the EXACT SAME CHARACTER.
               - Hair color and hairstyle MUST BE IDENTICAL.
               - Eye color MUST BE IDENTICAL.
               - Outfit style, fabric, and color palette MUST BE IDENTICAL.
               - Facial features and aesthetic MUST BE IDENTICAL.

            OUTPUT FORMAT:
            You must output EXACTLY two lines starting with 'AVATAR:' and 'FULLBODY:' containing comma-separated English image prompt tags:

            AVATAR: masterpiece, best quality, {{coreGenderTag}}, close-up face portrait, face focus, expressive luminous eyes, gentle subtle expression, <exact hair>, <exact eyes>, <exact face>, <upper outfit details>, soft painterly anime lighting, ethereal atmospheric glow, pixiv trending, highly detailed, 8k
            FULLBODY: masterpiece, best quality, {{coreGenderTag}}, waist-up standing portrait, elegant posture, <exact same hair>, <exact same eyes>, <exact same face>, <exact same intricate outfit>, luxurious outfit details, ethereal magical lighting, cinematic atmospheric glow, soft rim light, glowing accents, luminous eyes, beautiful detailed face, pixiv trending, sharp focus, 8k

            Output ONLY these two lines.
            """;
    }
}
