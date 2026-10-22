using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddChatIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "matrix_room_id",
                table: "teams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rocket_chat_room_id",
                table: "teams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "matrix_room_id",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "matrix_space_id",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rocket_chat_room_id",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "use_matrix",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "use_rocket_chat",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "matrix_room_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "rocket_chat_room_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "matrix_room_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "matrix_space_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "rocket_chat_room_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_matrix",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_rocket_chat",
                table: "msels");
        }
    }
}
