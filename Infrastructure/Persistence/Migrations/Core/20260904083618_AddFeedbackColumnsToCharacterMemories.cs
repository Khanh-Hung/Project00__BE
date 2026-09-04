using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddFeedbackColumnsToCharacterMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "CharacterMemories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackFingerprint",
                table: "CharacterMemories",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackType",
                table: "CharacterMemories",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterMemories_CharacterId_ExecutionId",
                table: "CharacterMemories",
                columns: new[] { "CharacterId", "ExecutionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CharacterMemories_CharacterId_ExecutionId",
                table: "CharacterMemories");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "CharacterMemories");

            migrationBuilder.DropColumn(
                name: "FeedbackFingerprint",
                table: "CharacterMemories");

            migrationBuilder.DropColumn(
                name: "FeedbackType",
                table: "CharacterMemories");
        }
    }
}
