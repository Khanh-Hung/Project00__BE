using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class UpdatePersonalityIdentityToIncludeTraitKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId",
                table: "CharacterPersonalityAdaptations");

            migrationBuilder.DropIndex(
                name: "IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId",
                table: "CharacterPersonalityAdaptationEvidences");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptations",
                columns: new[] { "CharacterId", "ExecutionId", "TraitKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptationEvidences",
                columns: new[] { "CharacterId", "ExecutionId", "TraitKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptations");

            migrationBuilder.DropIndex(
                name: "IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId_TraitKey",
                table: "CharacterPersonalityAdaptationEvidences");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId",
                table: "CharacterPersonalityAdaptations",
                columns: new[] { "CharacterId", "ExecutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId",
                table: "CharacterPersonalityAdaptationEvidences",
                columns: new[] { "CharacterId", "ExecutionId" },
                unique: true);
        }
    }
}
