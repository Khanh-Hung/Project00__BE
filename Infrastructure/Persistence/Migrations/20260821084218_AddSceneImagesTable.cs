using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneImagesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SceneState",
                table: "ChatSessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SceneImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IdentityReferenceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PreviousSceneImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("PK_SceneImages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_SceneRevision",
                table: "SceneImages",
                columns: new[] { "SessionId", "SceneRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_TurnId",
                table: "SceneImages",
                column: "TurnId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneImages");

            migrationBuilder.DropColumn(
                name: "SceneState",
                table: "ChatSessions");
        }
    }
}
