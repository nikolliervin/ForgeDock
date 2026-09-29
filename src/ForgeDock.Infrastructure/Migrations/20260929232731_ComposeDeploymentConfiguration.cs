using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ComposeDeploymentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeFile",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ComposeService",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeploymentMode",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedComposeManifest",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceStatusJson",
                table: "Deployments",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeFile",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ComposeService",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DeploymentMode",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProtectedComposeManifest",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "ServiceStatusJson",
                table: "Deployments");
        }
    }
}
