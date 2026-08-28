using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterVisualIdentityAndVisualMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_SceneRevision_IsCurrent",
                table: "SceneImages");

            migrationBuilder.AddColumn<Guid>(
                name: "GenerationAttemptId",
                table: "SceneImages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationFingerprint",
                table: "SceneImages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStatus",
                table: "SceneImages",
                type: "text",
                nullable: false,
                defaultValue: "Current");

            migrationBuilder.AddColumn<Guid>(
                name: "PredecessorArtifactId",
                table: "SceneImages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "SceneImages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuarantinedAt",
                table: "SceneImages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisualRevision",
                table: "SceneImages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedAttemptId",
                table: "ImageGenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                table: "ImageGenerationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CurrentAttemptNumber",
                table: "ImageGenerationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "ImageGenerationJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "ImageGenerationJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OutboxMessageId",
                table: "ImageGenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QuarantinedAttemptId",
                table: "ImageGenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "ImageGenerationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "ImageGenerationJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "CharacterVisualMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisualProfileVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Context = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Tags = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    QualityScore = table.Column<float>(type: "real", nullable: true),
                    IdentityScore = table.Column<float>(type: "real", nullable: true),
                    FeatureScore = table.Column<float>(type: "real", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    IsSoftDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterVisualMemories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharacterVisualProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisualVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    PrimaryReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    FaceReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    EyeColor = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    HairColor = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SkinTone = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FacialFeatures = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PermanentMarks = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    BodyIdentity = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Hairstyle = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CurrentOutfit = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Makeup = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Accessories = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TemporaryAppearance = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    IsSoftDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterVisualProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharacterVisualReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisualProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReferenceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false, defaultValue: "SecondaryCanonical"),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "Active"),
                    IsCanonical = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SourceGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceVisualRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    IsSoftDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterVisualReferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageGenerationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    DerivedSeed = table.Column<long>(type: "bigint", nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    GenerationFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ProviderJobId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClaimedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IdentitySimilarity = table.Column<float>(type: "real", nullable: true),
                    FeatureScore = table.Column<float>(type: "real", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FailureCategory = table.Column<string>(type: "text", nullable: false),
                    AcceptedArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    IsSoftDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageGenerationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageGenerationAttempts_ImageGenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "ImageGenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageGenerationAttempts_SceneImages_AcceptedArtifactId",
                        column: x => x.AcceptedArtifactId,
                        principalTable: "SceneImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisualSessionStates",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    VisualRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    IsSoftDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisualSessionStates", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_VisualSessionStates_ImageGenerationJobs_CurrentGenerationJo~",
                        column: x => x.CurrentGenerationJobId,
                        principalTable: "ImageGenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VisualSessionStates_SceneImages_CurrentImageId",
                        column: x => x.CurrentImageId,
                        principalTable: "SceneImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_GenerationAttemptId",
                table: "SceneImages",
                column: "GenerationAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_GenerationFingerprint",
                table: "SceneImages",
                column: "GenerationFingerprint",
                unique: true,
                filter: "\"GenerationFingerprint\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_GenerationJobId",
                table: "SceneImages",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_PredecessorArtifactId",
                table: "SceneImages",
                column: "PredecessorArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_SceneRevision",
                table: "SceneImages",
                columns: new[] { "SessionId", "SceneRevision" },
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_VisualRevision",
                table: "SceneImages",
                columns: new[] { "SessionId", "VisualRevision" },
                unique: true,
                filter: "\"IsCurrent\" = true AND \"LifecycleStatus\" != 4");

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_AcceptedAttemptId",
                table: "ImageGenerationJobs",
                column: "AcceptedAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_QuarantinedAttemptId",
                table: "ImageGenerationJobs",
                column: "QuarantinedAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_Status_NextAttemptAt",
                table: "ImageGenerationJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualMemories_ArtifactId",
                table: "CharacterVisualMemories",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualMemories_CharacterId_ArtifactId",
                table: "CharacterVisualMemories",
                columns: new[] { "CharacterId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualMemories_CharacterId_SceneRevision",
                table: "CharacterVisualMemories",
                columns: new[] { "CharacterId", "SceneRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualMemories_CharacterId_VisualProfileVersion",
                table: "CharacterVisualMemories",
                columns: new[] { "CharacterId", "VisualProfileVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualProfiles_CharacterId",
                table: "CharacterVisualProfiles",
                column: "CharacterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualReferences_ArtifactId",
                table: "CharacterVisualReferences",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualReferences_CharacterId",
                table: "CharacterVisualReferences",
                column: "CharacterId",
                unique: true,
                filter: "\"IsCanonical\" = true AND \"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualReferences_CharacterId_ArtifactId",
                table: "CharacterVisualReferences",
                columns: new[] { "CharacterId", "ArtifactId" },
                unique: true,
                filter: "\"ArtifactId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualReferences_CharacterId_Status",
                table: "CharacterVisualReferences",
                columns: new[] { "CharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVisualReferences_CharacterId_Type",
                table: "CharacterVisualReferences",
                columns: new[] { "CharacterId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationAttempts_AcceptedArtifactId",
                table: "ImageGenerationAttempts",
                column: "AcceptedArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationAttempts_GenerationFingerprint",
                table: "ImageGenerationAttempts",
                column: "GenerationFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationAttempts_GenerationJobId_AttemptNumber",
                table: "ImageGenerationAttempts",
                columns: new[] { "GenerationJobId", "AttemptNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_VisualSessionStates_CurrentGenerationJobId",
                table: "VisualSessionStates",
                column: "CurrentGenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualSessionStates_CurrentImageId",
                table: "VisualSessionStates",
                column: "CurrentImageId");

            migrationBuilder.AddForeignKey(
                name: "FK_ImageGenerationJobs_ImageGenerationAttempts_AcceptedAttempt~",
                table: "ImageGenerationJobs",
                column: "AcceptedAttemptId",
                principalTable: "ImageGenerationAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ImageGenerationJobs_ImageGenerationAttempts_QuarantinedAtte~",
                table: "ImageGenerationJobs",
                column: "QuarantinedAttemptId",
                principalTable: "ImageGenerationAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SceneImages_ImageGenerationAttempts_GenerationAttemptId",
                table: "SceneImages",
                column: "GenerationAttemptId",
                principalTable: "ImageGenerationAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SceneImages_ImageGenerationJobs_GenerationJobId",
                table: "SceneImages",
                column: "GenerationJobId",
                principalTable: "ImageGenerationJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SceneImages_SceneImages_PredecessorArtifactId",
                table: "SceneImages",
                column: "PredecessorArtifactId",
                principalTable: "SceneImages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImageGenerationJobs_ImageGenerationAttempts_AcceptedAttempt~",
                table: "ImageGenerationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_ImageGenerationJobs_ImageGenerationAttempts_QuarantinedAtte~",
                table: "ImageGenerationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_SceneImages_ImageGenerationAttempts_GenerationAttemptId",
                table: "SceneImages");

            migrationBuilder.DropForeignKey(
                name: "FK_SceneImages_ImageGenerationJobs_GenerationJobId",
                table: "SceneImages");

            migrationBuilder.DropForeignKey(
                name: "FK_SceneImages_SceneImages_PredecessorArtifactId",
                table: "SceneImages");

            migrationBuilder.DropTable(
                name: "CharacterVisualMemories");

            migrationBuilder.DropTable(
                name: "CharacterVisualProfiles");

            migrationBuilder.DropTable(
                name: "CharacterVisualReferences");

            migrationBuilder.DropTable(
                name: "ImageGenerationAttempts");

            migrationBuilder.DropTable(
                name: "VisualSessionStates");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_GenerationAttemptId",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_GenerationFingerprint",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_GenerationJobId",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_PredecessorArtifactId",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_SceneRevision",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_VisualRevision",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_ImageGenerationJobs_AcceptedAttemptId",
                table: "ImageGenerationJobs");

            migrationBuilder.DropIndex(
                name: "IX_ImageGenerationJobs_QuarantinedAttemptId",
                table: "ImageGenerationJobs");

            migrationBuilder.DropIndex(
                name: "IX_ImageGenerationJobs_Status_NextAttemptAt",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "GenerationAttemptId",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "GenerationFingerprint",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "PredecessorArtifactId",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "QuarantinedAt",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "VisualRevision",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "AcceptedAttemptId",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "CurrentAttemptNumber",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "OutboxMessageId",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "QuarantinedAttemptId",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "ImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ImageGenerationJobs");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_SceneRevision_IsCurrent",
                table: "SceneImages",
                columns: new[] { "SessionId", "SceneRevision", "IsCurrent" });
        }
    }
}
