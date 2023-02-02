/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class citeactionsandroles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "cite_scoring_model_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cite_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    move_number = table.Column<int>(type: "integer", nullable: false),
                    inject_number = table.Column<int>(type: "integer", nullable: false),
                    action_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cite_actions", x => x.id);
                    table.ForeignKey(
                        name: "FK_cite_actions_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cite_actions_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cite_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cite_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_cite_roles_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cite_roles_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cite_actions_msel_id",
                table: "cite_actions",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_actions_team_id",
                table: "cite_actions",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_roles_msel_id",
                table: "cite_roles",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_roles_team_id",
                table: "cite_roles",
                column: "team_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cite_actions");

            migrationBuilder.DropTable(
                name: "cite_roles");

            migrationBuilder.DropColumn(
                name: "cite_scoring_model_id",
                table: "msels");
        }
    }
}
