using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class add_msel_oranizations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

           migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

           migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
