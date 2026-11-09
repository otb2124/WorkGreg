using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkGreg.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Keyword",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Companies",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Exclude",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Include",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Locations",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MinSalary",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RemoteOnly",
                table: "Profiles");

            migrationBuilder.AddColumn<int>(
                name: "SearchProfileId",
                table: "Sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SearchProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Keyword = table.Column<string>(type: "TEXT", nullable: false),
                    Include = table.Column<string>(type: "TEXT", nullable: false),
                    Exclude = table.Column<string>(type: "TEXT", nullable: false),
                    Companies = table.Column<string>(type: "TEXT", nullable: false),
                    Locations = table.Column<string>(type: "TEXT", nullable: false),
                    MinSalary = table.Column<int>(type: "INTEGER", nullable: true),
                    RemoteOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sources_SearchProfileId_Provider",
                table: "Sources",
                columns: new[] { "SearchProfileId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchProfiles_OwnerChatId",
                table: "SearchProfiles",
                column: "OwnerChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Sources_SearchProfileId_Provider",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SearchProfileId",
                table: "Sources");

            migrationBuilder.AddColumn<string>(
                name: "Keyword",
                table: "Sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Companies",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Exclude",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Include",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Locations",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "MinSalary",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteOnly",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
