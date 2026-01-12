using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class groups_and_roles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cite_roles");

            migrationBuilder.AddColumn<Guid>(
                name: "role_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cite_duties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    is_template = table.Column<bool>(type: "boolean", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cite_duties", x => x.id);
                    table.ForeignKey(
                        name: "FK_cite_duties_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cite_duties_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    all_permissions = table.Column<bool>(type: "boolean", nullable: false),
                    immutable = table.Column<bool>(type: "boolean", nullable: false),
                    permissions = table.Column<int[]>(type: "integer[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_group_memberships_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "system_roles",
                columns: new[] { "id", "all_permissions", "description", "immutable", "name", "permissions" },
                values: new object[,]
                {
                    { new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"), false, "Can view all MSELs, but cannot make any changes.", false, "Observer", new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25 } },
                    { new Guid("d80b73c3-95d7-4468-8650-c62bbd082507"), false, "Can create and manage their own MSELs.", false, "Content Developer", new[] { 1, 2, 3, 4 } },
                    { new Guid("f35e8fff-f996-4cba-b303-3ba515ad8d2f"), true, "Can perform all actions", true, "Administrator", new int[0] }
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_role_id",
                table: "users",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_duties_msel_id",
                table: "cite_duties",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_duties_team_id",
                table: "cite_duties",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_memberships_group_id_user_id",
                table: "group_memberships",
                columns: new[] { "group_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_memberships_user_id",
                table: "group_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_groups_name",
                table: "groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_roles_name",
                table: "system_roles",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_users_system_roles_role_id",
                table: "users",
                column: "role_id",
                principalTable: "system_roles",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_system_roles_role_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "cite_duties");

            migrationBuilder.DropTable(
                name: "group_memberships");

            migrationBuilder.DropTable(
                name: "system_roles");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropIndex(
                name: "IX_users_role_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "role_id",
                table: "users");

            migrationBuilder.CreateTable(
                name: "cite_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_template = table.Column<bool>(type: "boolean", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true)
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
                name: "IX_cite_roles_msel_id",
                table: "cite_roles",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_cite_roles_team_id",
                table: "cite_roles",
                column: "team_id");
        }
    }
}
