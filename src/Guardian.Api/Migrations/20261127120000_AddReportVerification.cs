using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Guardian.Api.Migrations;

[DbContext(typeof(GuardianDb))]
[Migration("20261127120000_AddReportVerification")]
public sealed class AddReportVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("verification", "analyses", type: "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("verification", "analyses");
}
