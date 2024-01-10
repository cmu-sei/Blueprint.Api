using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class useplayerflag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "use_player",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "use_player",
                table: "msels");
        }
    }
}
