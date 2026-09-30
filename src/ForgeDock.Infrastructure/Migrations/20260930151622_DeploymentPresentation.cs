using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentPresentation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "Logs",
                type: "text",
                nullable: false,
                defaultValue: "Build");

            migrationBuilder.AddColumn<string>(
                name: "CommitAuthor",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitMessage",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "Deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStage",
                table: "Deployments",
                type: "text",
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.AddColumn<string>(
                name: "RequestedCommit",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "Deployments",
                type: "timestamp with time zone",
                nullable: true);
            // Preserve useful timing and stage information for existing deployments.
            migrationBuilder.Sql("""
                UPDATE "Deployments" d SET "LastStage" = CASE WHEN d."State" = 'Failed'
                    THEN COALESCE((SELECT substring(l."Message" from 8) FROM "Logs" l
                        WHERE l."DeploymentId" = d."Id" AND l."Message" IN
                        ('Stage: Queued','Stage: Preparing','Stage: Cloning','Stage: Building',
                         'Stage: Starting','Stage: HealthChecking','Stage: Routing','Stage: Running')
                        ORDER BY l."Id" DESC LIMIT 1), 'Preparing') ELSE d."State" END,
                    "StartedAt" = (SELECT MIN(l."Timestamp") FROM "Logs" l
                        WHERE l."DeploymentId" = d."Id" AND l."Message" = 'Stage: Preparing'),
                    "FinishedAt" = CASE WHEN d."State" IN ('Running','Stopped')
                        THEN COALESCE((SELECT MIN(l."Timestamp") FROM "Logs" l
                            WHERE l."DeploymentId" = d."Id" AND l."Message" = 'Stage: Running'), d."UpdatedAt")
                        WHEN d."State" = 'Failed' THEN d."UpdatedAt" ELSE NULL END;
                UPDATE "Logs" l SET "Phase" = 'Runtime' FROM
                    (SELECT "DeploymentId", MIN("Id") AS start_id FROM "Logs"
                     WHERE "Message" = 'Stage: Starting' GROUP BY "DeploymentId") starts
                    WHERE l."DeploymentId" = starts."DeploymentId" AND l."Id" >= starts.start_id;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Phase",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "CommitAuthor",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "CommitMessage",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "LastStage",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RequestedCommit",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "Deployments");
        }
    }
}
