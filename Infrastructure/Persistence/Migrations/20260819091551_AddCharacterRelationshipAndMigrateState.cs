using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterRelationshipAndMigrateState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create CharacterRelationships Table
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""CharacterRelationships"" (
                    ""Id"" uuid NOT NULL,
                    ""CharacterId"" uuid NOT NULL,
                    ""UserId"" uuid NOT NULL,
                    ""AffectionScore"" integer NOT NULL DEFAULT 0,
                    ""CurrentMood"" character varying(50) NOT NULL DEFAULT 'Neutral',
                    ""MoodIntensity"" integer NOT NULL DEFAULT 20,
                    ""LastInteractedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                    ""EventsJson"" text NOT NULL DEFAULT '[]',
                    ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                    ""CreatedBy"" text NULL,
                    ""UpdatedAt"" timestamp with time zone NULL,
                    ""UpdatedBy"" text NULL,
                    ""IsSoftDeleted"" boolean NOT NULL DEFAULT false,
                    ""DeletedAt"" timestamp with time zone NULL,
                    ""DeletedBy"" text NULL,
                    CONSTRAINT ""PK_CharacterRelationships"" PRIMARY KEY (""Id"")
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CharacterRelationships_UserId_CharacterId"" 
                ON ""CharacterRelationships"" (""UserId"", ""CharacterId"");
            ");

            // 2. Backfill existing state from ChatSessions to CharacterRelationships
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns 
                        WHERE table_name = 'ChatSessions' AND column_name = 'AffectionScore'
                    ) THEN
                        INSERT INTO ""CharacterRelationships"" (""Id"", ""CharacterId"", ""UserId"", ""AffectionScore"", ""CurrentMood"", ""MoodIntensity"", ""EventsJson"", ""LastInteractedAt"", ""CreatedAt"", ""UpdatedAt"", ""IsSoftDeleted"")
                        SELECT 
                            gen_random_uuid(),
                            cs.""CharacterId"",
                            cs.""UserId"",
                            cs.""AffectionScore"",
                            COALESCE(NULLIF(cs.""CurrentMood"", ''), 'Neutral'),
                            50,
                            '[]',
                            COALESCE(cs.""UpdatedAt"", cs.""CreatedAt"", now()),
                            cs.""CreatedAt"",
                            COALESCE(cs.""UpdatedAt"", cs.""CreatedAt""),
                            false
                        FROM (
                            SELECT DISTINCT ON (""UserId"", ""CharacterId"") *
                            FROM ""ChatSessions""
                            WHERE ""UserId"" IS NOT NULL
                            ORDER BY ""UserId"", ""CharacterId"", ""CreatedAt"" DESC
                        ) cs
                        ON CONFLICT (""UserId"", ""CharacterId"") DO NOTHING;
                    END IF;
                END $$;
            ");

            // 3. Drop deprecated columns from ChatSessions
            migrationBuilder.Sql(@"
                ALTER TABLE ""ChatSessions"" DROP COLUMN IF EXISTS ""AffectionScore"";
                ALTER TABLE ""ChatSessions"" DROP COLUMN IF EXISTS ""CurrentMood"";
                ALTER TABLE ""ChatSessions"" DROP COLUMN IF EXISTS ""RelationshipLevel"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterRelationships");

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
        }
    }
}
