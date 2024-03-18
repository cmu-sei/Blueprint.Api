/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

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

            migrationBuilder.Sql("INSERT INTO card_teams(team_id, card_id, can_post_articles, is_shown_on_wall) SELECT b.id, a.card_id, a.can_post_articles, a.is_shown_on_wall FROM card_teams a JOIN teams b ON a.team_id = b.old_team_id;");
            migrationBuilder.Sql("INSERT INTO player_application_teams(team_id, player_application_id) SELECT b.id, a.player_application_id FROM player_application_teams a JOIN teams b ON a.team_id = b.old_team_id;");
            migrationBuilder.Sql("INSERT INTO cite_actions(msel_id, team_id, move_number, inject_number, action_number, description, date_created, date_modified, created_by, modified_by, is_template) SELECT a.msel_id, b.id, a.move_number, a.inject_number, a.action_number, a.description, a.date_created, a.date_modified, a.created_by, a.modified_by, a.is_template FROM cite_actions a JOIN teams b ON a.team_id = b.old_team_id;");
            migrationBuilder.Sql("INSERT INTO cite_roles(msel_id, team_id, name, date_created, date_modified, created_by, modified_by, is_template) SELECT a.msel_id, b.id, a.name, a.date_created, a.date_modified, a.created_by, a.modified_by, a.is_template FROM cite_roles a JOIN teams b ON a.team_id = b.old_team_id;");

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
