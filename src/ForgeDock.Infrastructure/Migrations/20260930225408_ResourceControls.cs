using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ResourceControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuLimit",
                table: "Projects",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<int>(
                name: "MemoryLimitMiB",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 512);

            migrationBuilder.AddColumn<bool>(
                name: "ResourceAlertsEnabled",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ResourceAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceAlerts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourceObservations",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RestartBaseline = table.Column<int>(type: "integer", nullable: false),
                    ExitedChecks = table.Column<int>(type: "integer", nullable: false),
                    OomObserved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceObservations", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ResourceObservations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAlerts_ProjectId_CreatedAt",
                table: "ResourceAlerts",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceAlerts");

            migrationBuilder.DropTable(
                name: "ResourceObservations");

            migrationBuilder.DropColumn(
                name: "CpuLimit",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "MemoryLimitMiB",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ResourceAlertsEnabled",
                table: "Projects");
        }
    }
}
