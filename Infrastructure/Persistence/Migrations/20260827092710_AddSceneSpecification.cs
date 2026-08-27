using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneSpecification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SceneSpecifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Location = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Action = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Pose = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Environment = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Lighting = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Camera = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Weather = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TimeOfDay = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Mood = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    OutfitContext = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Objects = table.Column<string>(type: "text", nullable: false),
                    AtmosphereElements = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("PK_SceneSpecifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpecifications_CharacterId",
                table: "SceneSpecifications",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpecifications_CharacterId_SessionId_TurnId_SceneRevis~",
                table: "SceneSpecifications",
                columns: new[] { "CharacterId", "SessionId", "TurnId", "SceneRevision" },
                unique: true,
                filter: "\"SessionId\" IS NOT NULL AND \"TurnId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpecifications_SessionId",
                table: "SceneSpecifications",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneSpecifications");
        }
    }
}
