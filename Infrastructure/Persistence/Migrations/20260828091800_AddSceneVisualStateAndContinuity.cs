using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneVisualStateAndContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceTurnId",
                table: "CharacterVisualMemories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ValidFromTurnId",
                table: "CharacterVisualMemories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ValidUntilTurnId",
                table: "CharacterVisualMemories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidFromRevision",
                table: "CharacterVisualMemories",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ValidUntilRevision",
                table: "CharacterVisualMemories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outfit",
                table: "CharacterVisualMemories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hairstyle",
                table: "CharacterVisualMemories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Confidence",
                table: "CharacterVisualMemories",
                type: "real",
                nullable: false,
                defaultValue: 1.0f);

            migrationBuilder.CreateTable(
                name: "SceneVisualStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValidFromTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValidUntilTurnId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_SceneVisualStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneVisualStates_CharacterId",
                table: "SceneVisualStates",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneVisualStates_Fingerprint",
                table: "SceneVisualStates",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_SceneVisualStates_SessionId",
                table: "SceneVisualStates",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneVisualStates_SessionId_SceneKey",
                table: "SceneVisualStates",
                columns: new[] { "SessionId", "SceneKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneVisualStates_SessionId_CharacterId_SceneRevision",
                table: "SceneVisualStates",
                columns: new[] { "SessionId", "CharacterId", "SceneRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneVisualStates");

            migrationBuilder.DropColumn(
                name: "SourceTurnId",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "ValidFromTurnId",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "ValidUntilTurnId",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "ValidFromRevision",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "ValidUntilRevision",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "Outfit",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "Hairstyle",
                table: "CharacterVisualMemories");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "CharacterVisualMemories");
        }
    }
}
