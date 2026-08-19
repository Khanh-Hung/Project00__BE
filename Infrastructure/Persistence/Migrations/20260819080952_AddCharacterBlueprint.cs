using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterBlueprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AffectionScore",
                table: "ChatSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CurrentMood",
                table: "ChatSessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RelationshipLevel",
                table: "ChatSessions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Blueprint",
                table: "Characters",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomMilestonesJson",
                table: "Characters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultAffectionScore",
                table: "Characters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DefaultMood",
                table: "Characters",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AffectionScore",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "CurrentMood",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "RelationshipLevel",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "Blueprint",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "CustomMilestonesJson",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "DefaultAffectionScore",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "DefaultMood",
                table: "Characters");
        }
    }
}
