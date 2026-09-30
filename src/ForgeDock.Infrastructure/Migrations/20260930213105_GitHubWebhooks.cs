using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GitHubWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "Deployments",
                type: "text",
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "ProjectWebhooks",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWebhooks", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectWebhooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Event = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => new { x.ProjectId, x.DeliveryId });
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_ProjectId_ReceivedAt",
                table: "WebhookDeliveries",
                columns: new[] { "ProjectId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectWebhooks");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "Deployments");
        }
    }
}
