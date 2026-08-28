using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisualIdentityToCharacter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VisualIdentity",
                table: "Characters",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" DROP DEFAULT;
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" TYPE jsonb USING "EventsJson"::jsonb;
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" SET DEFAULT '[]'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VisualIdentity",
                table: "Characters");

            migrationBuilder.Sql("""
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" DROP DEFAULT;
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" TYPE text USING "EventsJson"::text;
                ALTER TABLE "CharacterRelationships" ALTER COLUMN "EventsJson" SET DEFAULT '[]';
                """);
        }
    }
}
