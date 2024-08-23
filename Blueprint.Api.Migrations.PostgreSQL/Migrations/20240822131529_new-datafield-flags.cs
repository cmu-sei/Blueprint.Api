using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class newdatafieldflags : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "is_initially_hidden",
                table: "data_fields",
                newName: "is_shown_on_default_tab");

            migrationBuilder.AddColumn<bool>(
                name: "is_facilitation_field",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_information_field",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_facilitation_field",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "is_information_field",
                table: "data_fields");

            migrationBuilder.RenameColumn(
                name: "is_shown_on_default_tab",
                table: "data_fields",
                newName: "is_initially_hidden");
        }
    }
}
