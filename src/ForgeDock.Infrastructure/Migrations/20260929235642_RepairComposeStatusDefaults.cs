using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForgeDock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepairComposeStatusDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Deployments\" SET \"ServiceStatusJson\" = '[]' WHERE btrim(\"ServiceStatusJson\") = '';");
            migrationBuilder.Sql("UPDATE \"Projects\" SET \"DeploymentMode\" = 'Dockerfile' WHERE \"DeploymentMode\" = '';");
            migrationBuilder.AlterColumn<string>(
                name: "ServiceStatusJson",
                table: "Deployments",
                type: "text",
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ServiceStatusJson",
                table: "Deployments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "[]");
        }
    }
}
