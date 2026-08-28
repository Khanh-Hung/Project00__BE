using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Project.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioArtifactsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudioArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VoiceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CleanedText = table.Column<string>(type: "text", nullable: false),
                    ContextHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AudioUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AudioFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
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
                    table.PrimaryKey("PK_AudioArtifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioArtifacts_ContextHash",
                table: "AudioArtifacts",
                column: "ContextHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AudioArtifacts_SessionId",
                table: "AudioArtifacts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioArtifacts_TurnId",
                table: "AudioArtifacts",
                column: "TurnId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioArtifacts");
        }
    }
}
