using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OffHostBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RemoteBucket",
                table: "DatabaseBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteEndpoint",
                table: "DatabaseBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteError",
                table: "DatabaseBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteKey",
                table: "DatabaseBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemoteNextAttemptAt",
                table: "DatabaseBackups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteRegion",
                table: "DatabaseBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteState",
                table: "DatabaseBackups",
                type: "text",
                nullable: false,
                defaultValue: "Disabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemoteBucket",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteEndpoint",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteError",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteKey",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteNextAttemptAt",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteRegion",
                table: "DatabaseBackups");

            migrationBuilder.DropColumn(
                name: "RemoteState",
                table: "DatabaseBackups");
        }
    }
}
