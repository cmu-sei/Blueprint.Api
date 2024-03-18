/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class invitations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cite_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "gallery_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "player_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "steamfitter_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "can_team_leader_invite",
                table: "msel_teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_team_member_invite",
                table: "msel_teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "cite_team_id",
                table: "msel_teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "gallery_team_id",
                table: "msel_teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "player_team_id",
                table: "msel_teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_domain = table.Column<string>(type: "text", nullable: true),
                    expiration_date_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_users_allowed = table.Column<int>(type: "integer", nullable: false),
                    user_count = table.Column<int>(type: "integer", nullable: false),
                    is_team_leader = table.Column<bool>(type: "boolean", nullable: false),
                    was_deactivated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                    table.ForeignKey(
                        name: "FK_invitations_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitations_teams_msel_id",
                        column: x => x.msel_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_id",
                table: "invitations",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_msel_id",
                table: "invitations",
                column: "msel_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropColumn(
                name: "cite_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "gallery_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "player_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "steamfitter_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "can_team_leader_invite",
                table: "msel_teams");

            migrationBuilder.DropColumn(
                name: "can_team_member_invite",
                table: "msel_teams");

            migrationBuilder.DropColumn(
                name: "cite_team_id",
                table: "msel_teams");

            migrationBuilder.DropColumn(
                name: "gallery_team_id",
                table: "msel_teams");

            migrationBuilder.DropColumn(
                name: "player_team_id",
                table: "msel_teams");
        }
    }
}
