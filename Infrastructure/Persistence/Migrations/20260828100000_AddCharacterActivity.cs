using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShouldCreateVisualMoment = table.Column<bool>(type: "boolean", nullable: false),
                    SceneIntentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimeBucket = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecisionFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
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
                    table.PrimaryKey("PK_CharacterActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterActivities_CharacterId_CreatedAt",
                table: "CharacterActivities",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterActivities_CharacterId_Status",
                table: "CharacterActivities",
                columns: new[] { "CharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterActivities_CharacterId_TimeBucket",
                table: "CharacterActivities",
                columns: new[] { "CharacterId", "TimeBucket" },
                unique: true,
                filter: "\"Source\" = 'Autonomous' OR \"Source\" = '1'");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterActivities_SceneIntentId",
                table: "CharacterActivities",
                column: "SceneIntentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterActivities");
        }
    }
}
