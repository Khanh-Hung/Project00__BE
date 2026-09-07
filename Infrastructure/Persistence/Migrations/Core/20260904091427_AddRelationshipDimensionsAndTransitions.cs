using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddRelationshipDimensionsAndTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CharacterRelationships_UserId_CharacterId",
                table: "CharacterRelationships");

            migrationBuilder.AddColumn<int>(
                name: "Affection",
                table: "CharacterRelationships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Familiarity",
                table: "CharacterRelationships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RelationshipType",
                table: "CharacterRelationships",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Stranger");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetId",
                table: "CharacterRelationships",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "CharacterRelationships",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.AddColumn<int>(
                name: "Trust",
                table: "CharacterRelationships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing rows: map TargetType = 'User', TargetId = UserId, and clamp AffectionScore into Affection (0..100)
            migrationBuilder.Sql(@"
                UPDATE ""CharacterRelationships""
                SET ""TargetType"" = 'User',
                    ""TargetId"" = ""UserId"",
                    ""Affection"" = CASE 
                        WHEN ""AffectionScore"" < 0 THEN 0 
                        WHEN ""AffectionScore"" > 100 THEN 100 
                        ELSE ""AffectionScore"" 
                    END;
            ");

            migrationBuilder.CreateTable(
                name: "CharacterRelationshipTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransitionFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrustDelta = table.Column<int>(type: "integer", nullable: false),
                    AffectionDelta = table.Column<int>(type: "integer", nullable: false),
                    FamiliarityDelta = table.Column<int>(type: "integer", nullable: false),
                    OldRelationshipType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NewRelationshipType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VersionBefore = table.Column<long>(type: "bigint", nullable: false),
                    VersionAfter = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("PK_CharacterRelationshipTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_CharacterId_TargetType_TargetId",
                table: "CharacterRelationships",
                columns: new[] { "CharacterId", "TargetType", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_UserId_CharacterId",
                table: "CharacterRelationships",
                columns: new[] { "UserId", "CharacterId" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationshipTransitions_CharacterId_AppliedAtUtc",
                table: "CharacterRelationshipTransitions",
                columns: new[] { "CharacterId", "AppliedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationshipTransitions_CharacterId_ExecutionId",
                table: "CharacterRelationshipTransitions",
                columns: new[] { "CharacterId", "ExecutionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterRelationshipTransitions");

            migrationBuilder.DropIndex(
                name: "IX_CharacterRelationships_CharacterId_TargetType_TargetId",
                table: "CharacterRelationships");

            migrationBuilder.DropIndex(
                name: "IX_CharacterRelationships_UserId_CharacterId",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "Affection",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "Familiarity",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "RelationshipType",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "CharacterRelationships");

            migrationBuilder.DropColumn(
                name: "Trust",
                table: "CharacterRelationships");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_UserId_CharacterId",
                table: "CharacterRelationships",
                columns: new[] { "UserId", "CharacterId" },
                unique: true);
        }
    }
}
