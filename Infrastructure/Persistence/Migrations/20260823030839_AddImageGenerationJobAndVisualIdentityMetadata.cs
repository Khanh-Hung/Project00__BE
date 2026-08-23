using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImageGenerationJobAndVisualIdentityMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_SceneRevision",
                table: "SceneImages");

            migrationBuilder.AddColumn<Guid>(
                name: "GenerationJobId",
                table: "SceneImages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GenerationRequestId",
                table: "SceneImages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "SceneImages",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Workflow",
                table: "SceneImages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "VisualIdentity");

            migrationBuilder.AddColumn<int>(
                name: "WorkflowVersion",
                table: "SceneImages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Safe Backfill: Populate existing historical SceneImages with GenerationRequestId = TurnId
            migrationBuilder.Sql("UPDATE \"SceneImages\" SET \"GenerationRequestId\" = \"TurnId\" WHERE \"GenerationRequestId\" IS NULL OR \"GenerationRequestId\" = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("UPDATE \"SceneImages\" SET \"Workflow\" = 'VisualIdentity', \"WorkflowVersion\" = 1 WHERE \"Workflow\" = '' OR \"Workflow\" IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "GenerationRequestId",
                table: "SceneImages",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateTable(
                name: "ImageGenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneRevision = table.Column<int>(type: "integer", nullable: false),
                    GenerationRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderJobId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ClaimedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsRetryable = table.Column<bool>(type: "boolean", nullable: false),
                    Workflow = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkflowVersion = table.Column<int>(type: "integer", nullable: false),
                    GenerationMetadataJson = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_ImageGenerationJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_GenerationRequestId",
                table: "SceneImages",
                columns: new[] { "SessionId", "GenerationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_SceneRevision_IsCurrent",
                table: "SceneImages",
                columns: new[] { "SessionId", "SceneRevision", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_SessionId_GenerationRequestId",
                table: "ImageGenerationJobs",
                columns: new[] { "SessionId", "GenerationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_SessionId_TurnId_SceneRevision",
                table: "ImageGenerationJobs",
                columns: new[] { "SessionId", "TurnId", "SceneRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageGenerationJobs_Status_LeaseUntil",
                table: "ImageGenerationJobs",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageGenerationJobs");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_GenerationRequestId",
                table: "SceneImages");

            migrationBuilder.DropIndex(
                name: "IX_SceneImages_SessionId_SceneRevision_IsCurrent",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "GenerationJobId",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "GenerationRequestId",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "Workflow",
                table: "SceneImages");

            migrationBuilder.DropColumn(
                name: "WorkflowVersion",
                table: "SceneImages");

            migrationBuilder.CreateIndex(
                name: "IX_SceneImages_SessionId_SceneRevision",
                table: "SceneImages",
                columns: new[] { "SessionId", "SceneRevision" },
                unique: true);
        }
    }
}
