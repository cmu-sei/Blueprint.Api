using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddTechniqueFrameworks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "technique_framework_id",
                table: "technique_releases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "technique_frameworks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    domain = table.Column<string>(type: "text", nullable: true),
                    is_defensive = table.Column<bool>(type: "boolean", nullable: false),
                    format = table.Column<int>(type: "integer", nullable: false),
                    settings = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technique_frameworks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_technique_releases_technique_framework_id",
                table: "technique_releases",
                column: "technique_framework_id");

            migrationBuilder.CreateIndex(
                name: "IX_technique_frameworks_domain",
                table: "technique_frameworks",
                column: "domain",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_technique_releases_technique_frameworks_technique_framework~",
                table: "technique_releases",
                column: "technique_framework_id",
                principalTable: "technique_frameworks",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_technique_releases_technique_frameworks_technique_framework~",
                table: "technique_releases");

            migrationBuilder.DropTable(
                name: "technique_frameworks");

            migrationBuilder.DropIndex(
                name: "IX_technique_releases_technique_framework_id",
                table: "technique_releases");

            migrationBuilder.DropColumn(
                name: "technique_framework_id",
                table: "technique_releases");
        }
    }
}
