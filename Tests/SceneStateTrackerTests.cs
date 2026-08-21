using Domain.Entities;
using Domain.ValueObjects;
using Xunit;

namespace Tests;

public class SceneStateTrackerTests
{
    [Fact]
    public void VisualContinuity_Invariant_Nothing_Changes_Unless_Explicitly_Changed()
    {
        // 1. Initial Master State
        var state = new SessionSceneState(
            CurrentLocation: "Phòng khách Thánh điện",
            CurrentOutfit: "Váy lụa trắng viền vàng",
            CurrentTimeOfDay: "Hoàng hôn",
            CurrentPose: "Ngồi trang nhã",
            HeldItems: "Tách trà sen",
            Atmosphere: "Yên bình",
            LastUpdatedAt: DateTime.UtcNow
        );

        // Turn 2: Only Pose changes (Player sits beside character)
        var deltaPose = new SceneStateDelta(
            PoseChange: "Ngồi sát bên người chơi, mỉm cười nhẹ"
        );
        state = state.ApplyDelta(deltaPose);

        Assert.Equal("Phòng khách Thánh điện", state.CurrentLocation); // MUST NOT CHANGE
        Assert.Equal("Váy lụa trắng viền vàng", state.CurrentOutfit); // MUST NOT CHANGE
        Assert.Equal("Hoàng hôn", state.CurrentTimeOfDay);            // MUST NOT CHANGE
        Assert.Equal("Tách trà sen", state.HeldItems);               // MUST NOT CHANGE
        Assert.Equal("Ngồi sát bên người chơi, mỉm cười nhẹ", state.CurrentPose);

        // Turn 3: Character places tea cup onto table
        var deltaDropItem = new SceneStateDelta(
            HeldItemsChange: "none"
        );
        state = state.ApplyDelta(deltaDropItem);

        Assert.Equal("Phòng khách Thánh điện", state.CurrentLocation); // MUST NOT CHANGE
        Assert.Equal("Váy lụa trắng viền vàng", state.CurrentOutfit); // MUST NOT CHANGE
        Assert.Null(state.HeldItems);                                // EXPLICITLY REMOVED

        // Turn 4: Dialogue only (No visual changes)
        var deltaNoOp = new SceneStateDelta();
        state = state.ApplyDelta(deltaNoOp);

        Assert.Equal("Phòng khách Thánh điện", state.CurrentLocation); // MUST NOT CHANGE
        Assert.Equal("Váy lụa trắng viền vàng", state.CurrentOutfit); // MUST NOT CHANGE
        Assert.Null(state.HeldItems);                                // REMAINS REMOVED

        // Turn 5: Explicit Location transition to Garden
        var deltaMoveToGarden = new SceneStateDelta(
            LocationChange: "Vườn hoa Thần Điện",
            PoseChange: "Đi dạo giữa những luống hoa"
        );
        state = state.ApplyDelta(deltaMoveToGarden);

        Assert.Equal("Vườn hoa Thần Điện", state.CurrentLocation);    // EXPLICITLY CHANGED
        Assert.Equal("Váy lụa trắng viền vàng", state.CurrentOutfit); // OUTFIT MUST PERSIST
        Assert.Equal("Hoàng hôn", state.CurrentTimeOfDay);            // TIME MUST PERSIST
        Assert.Equal("Đi dạo giữa những luống hoa", state.CurrentPose);
    }

    [Fact]
    public void ChatSession_Persistence_And_Delta_Integration()
    {
        var session = new ChatSession(Guid.NewGuid(), Guid.NewGuid(), "Continuity Session");
        var initial = new SessionSceneState("Đại điện", "Thánh phục", "Bình minh", "Đứng", null, "Thiêng liêng", DateTime.UtcNow);

        session.UpdateSceneState(initial);
        Assert.NotNull(session.SceneState);
        Assert.Equal("Đại điện", session.SceneState.CurrentLocation);

        var nextState = session.SceneState.ApplyDelta(new SceneStateDelta(OutfitChange: "Đồ ngủ lụa"));
        session.UpdateSceneState(nextState);

        Assert.Equal("Đại điện", session.SceneState.CurrentLocation); // Preserved
        Assert.Equal("Đồ ngủ lụa", session.SceneState.CurrentOutfit); // Mutated
    }
}
