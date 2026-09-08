using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddCharacterGoalExecutionProgresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterGoalExecutionProgresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProgressDelta = table.Column<int>(type: "integer", nullable: false),
                    OldProgress = table.Column<int>(type: "integer", nullable: false),
                    NewProgress = table.Column<int>(type: "integer", nullable: false),
                    OperationFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
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
                    table.PrimaryKey("PK_CharacterGoalExecutionProgresses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterGoalExecutionProgresses_CharacterId_ExecutionId",
                table: "CharacterGoalExecutionProgresses",
                columns: new[] { "CharacterId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterGoalExecutionProgresses_GoalId_ExecutionId",
                table: "CharacterGoalExecutionProgresses",
                columns: new[] { "GoalId", "ExecutionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterGoalExecutionProgresses");
        }
    }
}
