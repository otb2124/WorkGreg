using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkGreg.Migrations
{
    /// <inheritdoc />
    public partial class RefactorRelationsAndUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE FROM Jobs WHERE SourceId NOT IN (SELECT Id FROM Sources);");
            migrationBuilder.Sql(@"
            DELETE FROM Jobs WHERE SourceId IN (
                SELECT Id FROM Sources WHERE SearchProfileId NOT IN (SELECT Id FROM SearchProfiles));");
            migrationBuilder.Sql(@"DELETE FROM Sources WHERE SearchProfileId NOT IN (SELECT Id FROM SearchProfiles);");


            migrationBuilder.DropTable(
                name: "Profiles");

            migrationBuilder.DropColumn(
                name: "Baselined",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "OwnerChatId",
                table: "Sources");

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<string>(type: "TEXT", nullable: false),
                    DigestHourUtc = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.ChatId);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Sources_SourceId",
                table: "Jobs",
                column: "SourceId",
                principalTable: "Sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Sources_SearchProfiles_SearchProfileId",
                table: "Sources",
                column: "SearchProfileId",
                principalTable: "SearchProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Sources_SourceId",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Sources_SearchProfiles_SearchProfileId",
                table: "Sources");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.AddColumn<bool>(
                name: "Baselined",
                table: "Sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "OwnerChatId",
                table: "Sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<string>(type: "TEXT", nullable: false),
                    DigestHourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.ChatId);
                });
        }
    }
}
