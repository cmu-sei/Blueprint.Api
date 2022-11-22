using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class mselPlayerViewId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "player_view_id",
                table: "msels",
                type: "uuid",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "player_view_id",
                table: "msels");
        }
    }
}
