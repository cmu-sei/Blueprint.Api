using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class fixinvitationfkindex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invitations_teams_msel_id",
                table: "invitations");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_team_id",
                table: "invitations",
                column: "team_id");

            migrationBuilder.AddForeignKey(
                name: "FK_invitations_teams_team_id",
                table: "invitations",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invitations_teams_team_id",
                table: "invitations");

            migrationBuilder.DropIndex(
                name: "IX_invitations_team_id",
                table: "invitations");

            migrationBuilder.AddForeignKey(
                name: "FK_invitations_teams_msel_id",
                table: "invitations",
                column: "msel_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
