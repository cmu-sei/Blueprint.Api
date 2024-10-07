/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class Deleteextraniousdata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams");

            migrationBuilder.DropForeignKey(
                name: "FK_msel_teams_msels_msel_id",
                table: "msel_teams");

            migrationBuilder.DropForeignKey(
                name: "FK_msel_teams_teams_team_id",
                table: "msel_teams");

            migrationBuilder.DropIndex(
                name: "IX_msel_teams_msel_id",
                table: "msel_teams");

            migrationBuilder.DropIndex(
                name: "IX_msel_teams_team_id_msel_id",
                table: "msel_teams");

            // remove extraneous data records created when adding contributor units
            migrationBuilder.Sql("DELETE FROM cite_actions WHERE id in (SELECT c.id FROM cite_actions c JOIN teams t ON c.team_id = t.id WHERE (t.msel_id is null OR c.msel_id != t.msel_id));");
            migrationBuilder.Sql("DELETE FROM cite_roles WHERE id in (SELECT c.id FROM cite_roles c JOIN teams t ON c.team_id = t.id WHERE (t.msel_id is null OR c.msel_id != t.msel_id));");
            migrationBuilder.Sql("DELETE FROM card_teams WHERE id in (SELECT ct.id FROM card_teams ct JOIN teams t ON ct.team_id = t.id JOIN cards c ON ct.card_id = c.id WHERE (t.msel_id is null OR c.msel_id != t.msel_id));");
            migrationBuilder.Sql("DELETE FROM msel_teams;");

        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
