using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddPersonalityAdaptationAndEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterPersonalities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Warmth = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    Openness = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    Assertiveness = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    Conscientiousness = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    SocialConfidence = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    TrustDisposition = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    EmotionalStability = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
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
                    table.PrimaryKey("PK_CharacterPersonalities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharacterPersonalityAdaptationEvidences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TraitKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Strength = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsApplied = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AdaptationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterPersonalityAdaptationEvidences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharacterPersonalityAdaptations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TraitKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ValueBefore = table.Column<int>(type: "integer", nullable: false),
                    ValueAfter = table.Column<int>(type: "integer", nullable: false),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    EvidenceCount = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterPersonalityAdaptations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterPersonalities_CharacterId",
                table: "CharacterPersonalities",
                column: "CharacterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptationEvidences",
                columns: new[] { "CharacterId", "ExecutionId", "TraitKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalityAdaptationEvidences_CharId_TraitKey_IsApplied",
                table: "CharacterPersonalityAdaptationEvidences",
                columns: new[] { "CharacterId", "TraitKey", "IsApplied" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptations",
                columns: new[] { "CharacterId", "ExecutionId", "TraitKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_TraitKey",
                table: "CharacterPersonalityAdaptations",
                columns: new[] { "CharacterId", "TraitKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterPersonalities");

            migrationBuilder.DropTable(
                name: "CharacterPersonalityAdaptationEvidences");

            migrationBuilder.DropTable(
                name: "CharacterPersonalityAdaptations");
        }
    }
}
