using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class TeamRoleEnumToString : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop unique index before conversion to avoid duplicates
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_user_team_roles_team_id_user_id_role"";
            ");

            // Convert int enum values to CITE TeamRole name strings
            migrationBuilder.Sql(@"
                ALTER TABLE user_team_roles ALTER COLUMN role TYPE text
                    USING CASE role
                        WHEN 80 THEN 'Member'
                        WHEN 90 THEN 'Submitter'
                        WHEN 100 THEN 'Submitter'
                        WHEN 110 THEN 'Contributor'
                        WHEN 120 THEN 'Submitter'
                        ELSE 'Member'
                    END;
            ");

            // Remove duplicate rows keeping the first one
            migrationBuilder.Sql(@"
                DELETE FROM user_team_roles
                WHERE id NOT IN (
                    SELECT MIN(id::text)::uuid
                    FROM user_team_roles
                    GROUP BY team_id, user_id, role
                );
            ");

            // Recreate unique index
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ""IX_user_team_roles_team_id_user_id_role""
                    ON user_team_roles (team_id, user_id, role);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "role",
                table: "user_team_roles",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
