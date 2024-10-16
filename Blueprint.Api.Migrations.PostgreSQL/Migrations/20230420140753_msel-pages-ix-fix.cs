// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class Mselpagesixfix : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_msel_pages_msel_id",
                table: "msel_pages");

            migrationBuilder.CreateIndex(
                name: "IX_msel_pages_id",
                table: "msel_pages",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_msel_pages_msel_id",
                table: "msel_pages",
                column: "msel_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_msel_pages_id",
                table: "msel_pages");

            migrationBuilder.DropIndex(
                name: "IX_msel_pages_msel_id",
                table: "msel_pages");

            migrationBuilder.CreateIndex(
                name: "IX_msel_pages_msel_id",
                table: "msel_pages",
                column: "msel_id",
                unique: true);
        }
    }
}
