/*
 Copyright 2022 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class GalleryArticleParameters : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gallery_article_parameter",
                table: "data_fields",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_initially_hidden",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_only_shown_to_owners",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gallery_article_parameter",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "is_initially_hidden",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "is_only_shown_to_owners",
                table: "data_fields");
        }
    }
}
