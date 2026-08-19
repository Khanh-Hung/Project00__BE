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
            migrationBuilder.Sql("ALTER TABLE \"ChatSessions\" ADD COLUMN IF NOT EXISTS \"AffectionScore\" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql("ALTER TABLE \"ChatSessions\" ADD COLUMN IF NOT EXISTS \"CurrentMood\" character varying(100) NOT NULL DEFAULT '';");
            migrationBuilder.Sql("ALTER TABLE \"ChatSessions\" ADD COLUMN IF NOT EXISTS \"RelationshipLevel\" integer NOT NULL DEFAULT 1;");
            migrationBuilder.Sql("ALTER TABLE \"Characters\" ADD COLUMN IF NOT EXISTS \"Blueprint\" text;");
            migrationBuilder.Sql("ALTER TABLE \"Characters\" ADD COLUMN IF NOT EXISTS \"CustomMilestonesJson\" text;");
            migrationBuilder.Sql("ALTER TABLE \"Characters\" ADD COLUMN IF NOT EXISTS \"DefaultAffectionScore\" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql("ALTER TABLE \"Characters\" ADD COLUMN IF NOT EXISTS \"DefaultMood\" character varying(100) NOT NULL DEFAULT '';");
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
