using Domain.Entities;
using Domain.ValueObjects;
using Xunit;

namespace Tests;

public class SceneStateTrackerTests
{
    [Fact]
    public void Test1_Keep_Outfit_And_Location_When_Delta_Has_No_Change()
    {
        // Initial persistent state at Turn 1
        var state = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            CurrentTimeOfDay: "Evening",
            HeldItems: "Porcelain Tea Cup",
            Atmosphere: "Peaceful",
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        // Turn 2: Player asks "Are you happy today?" -> No visual/spatial delta
        var noOpDelta = new SceneStateDelta();
        var nextState = state.ApplyDelta(noOpDelta);

        // Assert: Invariance "Nothing changes unless explicitly changed"
        Assert.Equal("Living Room", nextState.CurrentLocation);
        Assert.Equal("Sofa", nextState.CurrentPosition);
        Assert.Equal("White Dress", nextState.CurrentOutfit);
        Assert.Equal("Evening", nextState.CurrentTimeOfDay);
        Assert.Equal("Porcelain Tea Cup", nextState.HeldItems);
        Assert.Equal("Peaceful", nextState.Atmosphere);
        Assert.Equal(2, nextState.SceneRevision);
    }

    [Fact]
    public void Test2_Movement_Sofa_To_Window_Changes_Position_Only()
    {
        var state = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            CurrentTimeOfDay: "Evening",
            SceneRevision: 1
        );

        // Turn 2: Character stands up and walks to the window
        var moveDelta = new SceneStateDelta(
            PositionChange: "Beside Window",
            ActionChange: "Walking toward window",
            PoseChange: "Standing"
        );
        var nextState = state.ApplyDelta(moveDelta);

        // Assert: Position changed, Location and Outfit MUST remain identical
        Assert.Equal("Beside Window", nextState.CurrentPosition);
        Assert.Equal("Living Room", nextState.CurrentLocation);
        Assert.Equal("White Dress", nextState.CurrentOutfit);
        Assert.Equal("Evening", nextState.CurrentTimeOfDay);
        Assert.Equal(2, nextState.SceneRevision);
    }

    [Fact]
    public void Test3_Outfit_Change_Mutates_Outfit_While_Preserving_Location_And_Position()
    {
        var state = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            CurrentTimeOfDay: "Evening",
            SceneRevision: 1
        );

        // Turn 2: Character changes into Black Evening Gown
        var outfitDelta = new SceneStateDelta(
            OutfitChange: "Black Evening Gown",
            Evidence: "She changed into a black evening gown"
        );
        var nextState = state.ApplyDelta(outfitDelta);

        // Assert: Outfit is mutated, Spatial coordinates remain identical
        Assert.Equal("Black Evening Gown", nextState.CurrentOutfit);
        Assert.Equal("Living Room", nextState.CurrentLocation);
        Assert.Equal("Sofa", nextState.CurrentPosition);
        Assert.Equal("Evening", nextState.CurrentTimeOfDay);
        Assert.Equal(2, nextState.SceneRevision);
    }

    [Fact]
    public void Test4_ActionChange_And_ExpressionChange_Map_To_TransientVisualState_Without_Mutating_PersistentState()
    {
        var persistentState = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            CurrentTimeOfDay: "Evening",
            SceneRevision: 1
        );

        // Turn 2 Delta: Momentary action & expression (No spatial/outfit changes)
        var delta = new SceneStateDelta(
            PoseChange: "Standing",
            ActionChange: "Walking toward window",
            ExpressionChange: "Gentle smile"
        );

        // 1. Persistent SceneState applies delta: Location, Position, Outfit remain 100% UNTOUCHED
        var updatedPersistent = persistentState.ApplyDelta(delta);
        Assert.Equal("Living Room", updatedPersistent.CurrentLocation); // UNCHANGED
        Assert.Equal("Sofa", updatedPersistent.CurrentPosition);       // UNCHANGED
        Assert.Equal("White Dress", updatedPersistent.CurrentOutfit);  // UNCHANGED

        // 2. TransientVisualState receives the frame-level dynamics
        var transientFrame = TransientVisualState.FromDelta(delta, defaultPose: "Sitting", defaultExpression: "Neutral");
        Assert.Equal("Standing", transientFrame.Pose);
        Assert.Equal("Walking toward window", transientFrame.Action);
        Assert.Equal("Gentle smile", transientFrame.Expression);
    }

    [Fact]
    public void Test5_VisualSnapshot_Isolation_And_Deep_Immutability_Between_Turns()
    {
        var turnId1 = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var dna = new CharacterVisualIdentity(
            Gender: "Female",
            Face: "Delicate porcelain face, gentle smile",
            Hair: "Long platinum blonde hair",
            Eyes: "Emerald green eyes",
            Skin: "Fair skin",
            Body: "Slender athletic build",
            CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
        );

        var stateTurn1 = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            CurrentTimeOfDay: "Evening",
            SceneRevision: 1
        );

        var transientTurn1 = new TransientVisualState(
            Pose: "Sitting gracefully",
            Action: "Holding tea cup",
            Expression: "Warm tender smile"
        );

        // Freeze Turn 1 Snapshot
        var snapshot1 = VisualSnapshot.Create(
            turnId: turnId1,
            sessionId: sessionId,
            characterId: characterId,
            sceneRevision: 1,
            visualIdentity: dna,
            sceneState: stateTurn1,
            transientState: transientTurn1,
            generationProfile: GenerationProfile.CreateDefault(),
            previousSceneImageUrl: null
        );

        // Verify Identity Reference resolution: Canonical
        Assert.Equal("https://cloud.storage/elysia_canonical.png", snapshot1.IdentityReferenceUrl);

        // Turn 2 mutates SceneState to Garden with Red Dress
        var deltaTurn2 = new SceneStateDelta(
            LocationChange: "Royal Garden",
            PositionChange: "Stone Bench",
            OutfitChange: "Red Silk Dress",
            PoseChange: "Standing",
            ActionChange: "Picking a rose"
        );
        var stateTurn2 = stateTurn1.ApplyDelta(deltaTurn2, explicitRevision: 2);
        var transientTurn2 = TransientVisualState.FromDelta(deltaTurn2);

        var snapshot2 = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: sessionId,
            characterId: characterId,
            sceneRevision: 2,
            visualIdentity: dna,
            sceneState: stateTurn2,
            transientState: transientTurn2,
            generationProfile: GenerationProfile.CreateDefault(),
            previousSceneImageUrl: "https://cloud.storage/scene_turn1.png"
        );

        // Strict Isolation Assertion: Snapshot 1 MUST remain untouched!
        Assert.Equal(1, snapshot1.SceneRevision);
        Assert.Equal("Living Room", snapshot1.SceneState.CurrentLocation);
        Assert.Equal("Sofa", snapshot1.SceneState.CurrentPosition);
        Assert.Equal("White Dress", snapshot1.SceneState.CurrentOutfit);
        Assert.Equal("Sitting gracefully", snapshot1.TransientState?.Pose);
        Assert.Equal("Holding tea cup", snapshot1.TransientState?.Action);
        Assert.Null(snapshot1.PreviousSceneImageUrl);

        // Snapshot 2 reflects Turn 2 state
        Assert.Equal(2, snapshot2.SceneRevision);
        Assert.Equal("Royal Garden", snapshot2.SceneState.CurrentLocation);
        Assert.Equal("Stone Bench", snapshot2.SceneState.CurrentPosition);
        Assert.Equal("Red Silk Dress", snapshot2.SceneState.CurrentOutfit);
        Assert.Equal("Standing", snapshot2.TransientState?.Pose);
        Assert.Equal("Picking a rose", snapshot2.TransientState?.Action);
        Assert.Equal("https://cloud.storage/scene_turn1.png", snapshot2.PreviousSceneImageUrl);
    }

    [Fact]
    public void Test6_Revision_Sequencing_And_Explicit_Overrides()
    {
        var state = new SessionSceneState(
            CurrentLocation: "Sanctuary",
            CurrentOutfit: "Holy Robes",
            SceneRevision: 10
        );

        var nextStateAutoRev = state.ApplyDelta(new SceneStateDelta(OutfitChange: "Casual Wear"));
        Assert.Equal(11, nextStateAutoRev.SceneRevision);

        // Explicit revision assignment for authoritative application commit boundary
        var nextStateExplicitRev = state.ApplyDelta(new SceneStateDelta(OutfitChange: "Casual Wear"), explicitRevision: 42);
        Assert.Equal(42, nextStateExplicitRev.SceneRevision);
    }

    [Fact]
    public void Test7_HeldItems_Lifecycle_Preserves_Until_Explicitly_Cleared()
    {
        var state = new SessionSceneState(
            CurrentLocation: "Tea Room",
            CurrentOutfit: "Kimono",
            HeldItems: "Ceramic Tea Bowl",
            SceneRevision: 1
        );

        // Turn 2: Character talks, item remains held
        var talkDelta = new SceneStateDelta(PoseChange: "Nodding gently");
        var turn2 = state.ApplyDelta(talkDelta);
        Assert.Equal("Ceramic Tea Bowl", turn2.HeldItems);

        // Turn 3: Character explicitly places down bowl
        var dropDelta = new SceneStateDelta(HeldItemsChange: "placed_down");
        var turn3 = turn2.ApplyDelta(dropDelta);
        Assert.Null(turn3.HeldItems);

        // Turn 4: Next dialogue -> bowl stays cleared
        var turn4 = turn3.ApplyDelta(new SceneStateDelta());
        Assert.Null(turn4.HeldItems);
    }

    [Fact]
    public void Test8_Initial_State_Starts_At_Revision_0_And_First_Turn_Becomes_Revision_1()
    {
        // Uncommitted initial state
        var initialState = new SessionSceneState(
            CurrentLocation: "Grand Temple",
            CurrentOutfit: "Holy Robes"
        );
        Assert.Equal(0, initialState.SceneRevision);

        // Turn 1 commit
        var turn1State = initialState.ApplyDelta(new SceneStateDelta(PoseChange: "Praying"), explicitRevision: 1);
        Assert.Equal(1, turn1State.SceneRevision);

        // Turn 2 commit
        var turn2State = turn1State.ApplyDelta(new SceneStateDelta(PositionChange: "Altar"), explicitRevision: 2);
        Assert.Equal(2, turn2State.SceneRevision);
    }

    [Fact]
    public void Test9_Structured_VisualSceneDescription_In_Delta_Populates_Snapshot_And_Preserves_Continuity()
    {
        var persistentState = new SessionSceneState(
            CurrentLocation: "Clockwork Workshop",
            CurrentPosition: "Assembly Table",
            CurrentOutfit: "Leather Apron and White Shirt",
            CurrentTimeOfDay: "Afternoon",
            HeldItems: "Brass Screwdriver",
            Atmosphere: "Focused",
            SceneRevision: 2
        );

        var sceneDesc = new VisualSceneDescription(
            shotType: "medium shot",
            cameraAngle: "slight 3/4 turn",
            subjectPlacement: "centered",
            detailedAction: "standing at assembly table adjusting brass clockwork gear",
            detailedEnvironment: "clockwork workshop, copper gears and tools on table",
            lightingStyle: "warm afternoon sun streaming through dusty window",
            atmosphere: "focused, intricate craftsmanship",
            englishPromptTags: new[] { "medium shot", "clockwork workshop", "adjusting gear" }
        );

        var delta = new SceneStateDelta(
            PoseChange: "Standing leaning forward",
            ActionChange: "Adjusting gear",
            SceneDescription: sceneDesc
        );

        var nextState = persistentState.ApplyDelta(delta);
        var transient = TransientVisualState.FromDelta(delta);

        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: nextState.SceneRevision,
            visualIdentity: null,
            sceneState: nextState,
            transientState: transient,
            generationProfile: GenerationProfile.CreateDefault(seed: 999),
            sceneDescription: delta.SceneDescription
        );

        Assert.NotNull(snapshot.SceneDescription);
        Assert.Equal("medium shot", snapshot.SceneDescription.ShotType);
        Assert.Equal("Clockwork Workshop", snapshot.SceneState.CurrentLocation);
        Assert.Equal("Leather Apron and White Shirt", snapshot.SceneState.CurrentOutfit);
        Assert.Equal("Brass Screwdriver", snapshot.SceneState.HeldItems);
        Assert.Equal("Standing leaning forward", snapshot.TransientState?.Pose);
    }
}
