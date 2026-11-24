using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guardian.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewerHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reviewers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => new { x.project_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_reviewers_user_id",
                        column: x => x.user_id,
                        principalTable: "reviewers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_findings_analysis_id_rule_id_ordinal",
                table: "findings",
                columns: new[] { "analysis_id", "rule_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_findings_analysis_id_severity_ordinal",
                table: "findings",
                columns: new[] { "analysis_id", "severity", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_analyses_project_id_created_at_id",
                table: "analyses",
                columns: new[] { "project_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_project_members_user_id",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reviewers_email",
                table: "reviewers",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "reviewers");

            migrationBuilder.DropIndex(
                name: "IX_findings_analysis_id_rule_id_ordinal",
                table: "findings");

            migrationBuilder.DropIndex(
                name: "IX_findings_analysis_id_severity_ordinal",
                table: "findings");

            migrationBuilder.DropIndex(
                name: "IX_analyses_project_id_created_at_id",
                table: "analyses");
        }
    }
}
