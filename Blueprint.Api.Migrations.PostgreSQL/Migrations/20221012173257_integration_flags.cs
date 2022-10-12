using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class integration_flags : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.AddColumn<Guid>(
                name: "gallery_collection_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "use_cite",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "use_gallery",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "use_steamfitter",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "gallery_collection_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_cite",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_gallery",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_steamfitter",
                table: "msels");

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
