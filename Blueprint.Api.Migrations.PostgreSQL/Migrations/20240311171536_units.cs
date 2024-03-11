using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class units : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.RenameColumn(
                name: "is_participant_team",
                table: "teams",
                newName: "can_team_member_invite");

            migrationBuilder.AddColumn<bool>(
                name: "can_team_leader_invite",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "cite_team_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cite_team_type_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "teams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "gallery_team_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "msel_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "old_team_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    short_name = table.Column<string>(type: "text", nullable: true),
                    old_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "msel_units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_units", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_units_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_msel_units_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "unit_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unit_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_unit_users_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_unit_users_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_teams_msel_id",
                table: "teams",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_units_msel_id",
                table: "msel_units",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_units_unit_id_msel_id",
                table: "msel_units",
                columns: new[] { "unit_id", "msel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unit_users_unit_id",
                table: "unit_users",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_unit_users_user_id_unit_id",
                table: "unit_users",
                columns: new[] { "user_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_units_id",
                table: "units",
                column: "id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");

            migrationBuilder.Sql("INSERT INTO units(name, short_name, old_team_id, date_created, date_modified, created_by, modified_by) SELECT name, short_name, id, date_created, date_modified, created_by, modified_by FROM teams;");
            migrationBuilder.Sql("INSERT INTO unit_users(user_id, unit_id) SELECT a.user_id, b.id FROM team_users a JOIN units b ON a.team_id = b.old_team_id;");
            migrationBuilder.Sql("INSERT INTO msel_units(unit_id, msel_id) SELECT b.id, a.msel_id FROM msel_teams a JOIN units b ON a.team_id = b.old_team_id;");
            migrationBuilder.Sql("INSERT INTO teams(name, short_name, date_created, date_modified, created_by, modified_by, can_team_member_invite, player_team_id, can_team_leader_invite, cite_team_id, cite_team_type_id, email, gallery_team_id, msel_id, old_team_id) SELECT a.name, a.short_name, a.date_created, a.date_modified, a.created_by, a.modified_by, a.can_team_member_invite, a.player_team_id, a.can_team_leader_invite, a.cite_team_id, a.cite_team_type_id, a.email, a.gallery_team_id, b.msel_id, a.id FROM teams a JOIN msel_teams b ON a.id = b.team_id;");
            migrationBuilder.Sql("INSERT INTO team_users(user_id, team_id) SELECT c.user_id, t.id as team_id FROM teams t JOIN (SELECT b.id as old_team_id, a.user_id from team_users a JOIN teams b ON a.team_id = b.id where b.old_team_id is null) c ON t.old_team_id = c.old_team_id;");

        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams");

            migrationBuilder.DropTable(
                name: "msel_units");

            migrationBuilder.DropTable(
                name: "unit_users");

            migrationBuilder.DropTable(
                name: "units");

            migrationBuilder.DropIndex(
                name: "IX_teams_msel_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "can_team_leader_invite",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "cite_team_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "cite_team_type_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "email",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "gallery_team_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "msel_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "old_team_id",
                table: "teams");

            migrationBuilder.RenameColumn(
                name: "can_team_member_invite",
                table: "teams",
                newName: "is_participant_team");

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
        }
    }
}
