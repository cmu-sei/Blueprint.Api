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
            // Rename cite_roles to cite_duties to preserve existing data
            migrationBuilder.RenameTable(
                name: "cite_roles",
                newName: "cite_duties");

            // Rename primary key constraint
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"PK_cite_roles\" TO \"PK_cite_duties\";");

            // Rename foreign key constraints
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"FK_cite_roles_msels_msel_id\" TO \"FK_cite_duties_msels_msel_id\";");
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"FK_cite_roles_teams_team_id\" TO \"FK_cite_duties_teams_team_id\";");

            // Rename indexes
            migrationBuilder.RenameIndex(
                name: "IX_cite_roles_msel_id",
                table: "cite_duties",
                newName: "IX_cite_duties_msel_id");

            migrationBuilder.RenameIndex(
                name: "IX_cite_roles_team_id",
                table: "cite_duties",
                newName: "IX_cite_duties_team_id");

            migrationBuilder.AddColumn<Guid>(
                name: "role_id",
                table: "users",
                type: "uuid",
                nullable: true);

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

            // Rename indexes back
            migrationBuilder.RenameIndex(
                name: "IX_cite_duties_msel_id",
                table: "cite_duties",
                newName: "IX_cite_roles_msel_id");

            migrationBuilder.RenameIndex(
                name: "IX_cite_duties_team_id",
                table: "cite_duties",
                newName: "IX_cite_roles_team_id");

            // Rename foreign key constraints back
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"FK_cite_duties_msels_msel_id\" TO \"FK_cite_roles_msels_msel_id\";");
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"FK_cite_duties_teams_team_id\" TO \"FK_cite_roles_teams_team_id\";");

            // Rename primary key constraint back
            migrationBuilder.Sql("ALTER TABLE cite_duties RENAME CONSTRAINT \"PK_cite_duties\" TO \"PK_cite_roles\";");

            // Rename cite_duties back to cite_roles to preserve existing data
            migrationBuilder.RenameTable(
                name: "cite_duties",
                newName: "cite_roles");
        }
    }
}
