using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentHooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HookTimeoutSeconds",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<string>(
                name: "PostDeployCommand",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PreDeployCommand",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DeploymentHooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentHooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentHooks_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentHooks_DeploymentId_StartedAt",
                table: "DeploymentHooks",
                columns: new[] { "DeploymentId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentHooks");

            migrationBuilder.DropColumn(
                name: "HookTimeoutSeconds",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PostDeployCommand",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PreDeployCommand",
                table: "Projects");
        }
    }
}
