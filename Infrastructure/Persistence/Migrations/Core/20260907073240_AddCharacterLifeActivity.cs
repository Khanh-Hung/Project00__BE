using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddCharacterLifeActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterLifeActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlannedEndAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Metadata = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterLifeActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterLifeActivities_CharacterId_PlannedEndAtUtc",
                table: "CharacterLifeActivities",
                columns: new[] { "CharacterId", "PlannedEndAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterLifeActivities_CharacterId_StartAtUtc",
                table: "CharacterLifeActivities",
                columns: new[] { "CharacterId", "StartAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterLifeActivities_CharacterId_Status",
                table: "CharacterLifeActivities",
                columns: new[] { "CharacterId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterLifeActivities");
        }
    }
}
