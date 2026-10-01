using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRollbackEnabled",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RollbackFailureThreshold",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RollbackWindowMinutes",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AutoRollbackTriggeredAt",
                table: "Deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthFailureCount",
                table: "Deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousDeploymentId",
                table: "Deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RollbackDeadlineAt",
                table: "Deployments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoRollbackEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RollbackFailureThreshold",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RollbackWindowMinutes",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AutoRollbackTriggeredAt",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "HealthFailureCount",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "PreviousDeploymentId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RollbackDeadlineAt",
                table: "Deployments");
        }
    }
}
