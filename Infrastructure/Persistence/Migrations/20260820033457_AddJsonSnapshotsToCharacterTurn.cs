using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJsonSnapshotsToCharacterTurn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveMemoriesJson",
                table: "CharacterTurns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventsJson",
                table: "CharacterTurns",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveMemoriesJson",
                table: "CharacterTurns");

            migrationBuilder.DropColumn(
                name: "EventsJson",
                table: "CharacterTurns");
        }
    }
}
