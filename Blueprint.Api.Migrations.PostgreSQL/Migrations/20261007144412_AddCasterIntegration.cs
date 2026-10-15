using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddCasterIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "caster_directory_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "caster_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "caster_run_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "caster_workspace_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "use_caster",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "caster_directory_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "caster_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "caster_run_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "caster_workspace_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_caster",
                table: "msels");
        }
    }
}
