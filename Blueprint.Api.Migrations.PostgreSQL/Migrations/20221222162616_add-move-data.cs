using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class addmovedata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "move_start_time",
                table: "moves",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "move_stop_time",
                table: "moves",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "situation_description",
                table: "moves",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "situation_time",
                table: "moves",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "moves",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "move_start_time",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "move_stop_time",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "situation_description",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "situation_time",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "title",
                table: "moves");
        }
    }
}
