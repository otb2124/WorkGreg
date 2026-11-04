using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkGreg.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceProviderKeyword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Keyword",
                table: "Sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Sources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Keyword",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Sources");
        }
    }
}
