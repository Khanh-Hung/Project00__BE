using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterWorldEventsAndReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterWorldEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_CharacterWorldEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharacterWorldEventReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorldEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerceptionType = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    ReactionReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MoodDelta = table.Column<int>(type: "integer", nullable: false),
                    EnergyDelta = table.Column<int>(type: "integer", nullable: false),
                    StressDelta = table.Column<int>(type: "integer", nullable: false),
                    HungerDelta = table.Column<int>(type: "integer", nullable: false),
                    SocialNeedDelta = table.Column<int>(type: "integer", nullable: false),
                    ConfidenceDelta = table.Column<int>(type: "integer", nullable: false),
                    RelationshipDelta = table.Column<int>(type: "integer", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: true),
                    GoalContribution = table.Column<double>(type: "double precision", nullable: true),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    TriggeredActivityType = table.Column<string>(type: "text", nullable: true),
                    VisualMomentCreated = table.Column<bool>(type: "boolean", nullable: false),
                    SceneIntentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SceneSpecificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("PK_CharacterWorldEventReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterWorldEventReactions_CharacterWorldEvents_WorldEventId",
                        column: x => x.WorldEventId,
                        principalTable: "CharacterWorldEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEvents_CharacterId_OccurredAt",
                table: "CharacterWorldEvents",
                columns: new[] { "CharacterId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEvents_CharacterId_EventType",
                table: "CharacterWorldEvents",
                columns: new[] { "CharacterId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEvents_CorrelationId",
                table: "CharacterWorldEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEventReactions_WorldEventId_CharacterId",
                table: "CharacterWorldEventReactions",
                columns: new[] { "WorldEventId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEventReactions_CharacterId_ProcessedAt",
                table: "CharacterWorldEventReactions",
                columns: new[] { "CharacterId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterWorldEventReactions_ExecutionId",
                table: "CharacterWorldEventReactions",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpecifications_CharacterId_SceneRevision",
                table: "SceneSpecifications",
                columns: new[] { "CharacterId", "SceneRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SceneSpecifications_CharacterId_SceneRevision",
                table: "SceneSpecifications");

            migrationBuilder.DropTable(name: "CharacterWorldEventReactions");
            migrationBuilder.DropTable(name: "CharacterWorldEvents");
        }
    }
}
