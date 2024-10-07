/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class Multiselectoptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_injects_catalogs_catalog_entity_id",
                table: "injects");

            migrationBuilder.DropIndex(
                name: "IX_injects_catalog_entity_id",
                table: "injects");

            migrationBuilder.DropColumn(
                name: "catalog_entity_id",
                table: "injects");

            migrationBuilder.AddColumn<bool>(
                name: "is_multi_select",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_multi_select",
                table: "data_fields");

            migrationBuilder.AddColumn<Guid>(
                name: "catalog_entity_id",
                table: "injects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_injects_catalog_entity_id",
                table: "injects",
                column: "catalog_entity_id");

            migrationBuilder.AddForeignKey(
                name: "FK_injects_catalogs_catalog_entity_id",
                table: "injects",
                column: "catalog_entity_id",
                principalTable: "catalogs",
                principalColumn: "id");
        }
    }
}
