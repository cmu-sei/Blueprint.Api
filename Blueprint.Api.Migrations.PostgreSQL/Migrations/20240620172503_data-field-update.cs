/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class datafieldupdate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields");

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "data_fields",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields",
                sql: "msel_id IS NULL OR inject_type_id IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "description",
                table: "data_fields");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields",
                sql: "(msel_id IS NOT NULL AND inject_type_id IS NULL) OR (msel_id IS NULL AND inject_type_id IS NOT NULL)");
        }
    }
}
