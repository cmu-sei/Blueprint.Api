/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class uniquedataValues : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_data_values_scenario_event_id",
                table: "data_values");

            migrationBuilder.CreateIndex(
                name: "IX_data_values_id",
                table: "data_values",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_data_values_scenario_event_id_data_field_id",
                table: "data_values",
                columns: new[] { "scenario_event_id", "data_field_id" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_data_values_id",
                table: "data_values");

            migrationBuilder.DropIndex(
                name: "IX_data_values_scenario_event_id_data_field_id",
                table: "data_values");

            migrationBuilder.CreateIndex(
                name: "IX_data_values_scenario_event_id",
                table: "data_values",
                column: "scenario_event_id");
        }
    }
}
