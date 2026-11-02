using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddAttackTechniqueCountermeasures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_defensive",
                table: "attack_releases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Releases imported before the flag existed: only D3FEND's ontology parser produced defensive ones.
            migrationBuilder.Sql("UPDATE attack_releases SET is_defensive = true WHERE domain = 'd3fend'");

            migrationBuilder.CreateTable(
                name: "attack_technique_countermeasures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offensive_external_id = table.Column<string>(type: "text", nullable: true),
                    defensive_technique_id = table.Column<Guid>(type: "uuid", nullable: false),
                    defensive_tactic = table.Column<string>(type: "text", nullable: true),
                    rationale = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attack_technique_countermeasures", x => x.id);
                    table.ForeignKey(
                        name: "FK_attack_technique_countermeasures_attack_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "attack_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attack_technique_countermeasures_attack_techniques_defensiv~",
                        column: x => x.defensive_technique_id,
                        principalTable: "attack_techniques",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attack_technique_countermeasures_defensive_technique_id",
                table: "attack_technique_countermeasures",
                column: "defensive_technique_id");

            migrationBuilder.CreateIndex(
                name: "IX_attack_technique_countermeasures_release_id_offensive_exter~",
                table: "attack_technique_countermeasures",
                columns: new[] { "release_id", "offensive_external_id", "defensive_technique_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attack_technique_countermeasures");

            migrationBuilder.DropColumn(
                name: "is_defensive",
                table: "attack_releases");
        }
    }
}
