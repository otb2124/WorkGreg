using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkGreg.Migrations
{
    /// <inheritdoc />
    public partial class SplitIngestFromDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Park the old per-source table (its indexes go first so their names are free for the new ones).
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_Jobs_SourceId_ExternalId;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_Jobs_OwnerChatId_Hash;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_Jobs_OwnerChatId_Processed;");
            migrationBuilder.Sql(@"ALTER TABLE Jobs RENAME TO Jobs_old;");

            // 2. New shared table: one row per (Provider, ExternalId).
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Salary = table.Column<int>(type: "INTEGER", nullable: true),
                    EmploymentType = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            // 3. New per-user table: one row per (source, job).
            migrationBuilder.CreateTable(
                name: "JobDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Processed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobDeliveries_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobDeliveries_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Provider_ExternalId",
                table: "Jobs",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Hash",
                table: "Jobs",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeliveries_JobId",
                table: "JobDeliveries",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeliveries_OwnerChatId_Processed",
                table: "JobDeliveries",
                columns: new[] { "OwnerChatId", "Processed" });

            migrationBuilder.CreateIndex(
                name: "IX_JobDeliveries_SourceId_JobId",
                table: "JobDeliveries",
                columns: new[] { "SourceId", "JobId" },
                unique: true);

            // 4. Copy the data. An old row's provider is its source's catalog id, or "source{Id}" for legacy raw-URL
            //    sources: the same key JobHasher used as its scope, so every stored Hash stays valid.
            migrationBuilder.Sql(@"
                CREATE TEMP TABLE JobKeys AS
                SELECT o.Id AS OldId,
                       COALESCE(s.Provider, 'source' || s.Id) AS Provider,
                       o.ExternalId AS ExternalId
                FROM Jobs_old o
                JOIN Sources s ON s.Id = o.SourceId;");
            migrationBuilder.Sql(@"CREATE INDEX JobKeys_Lookup ON JobKeys (Provider, ExternalId);");

            // The same vacancy stored by several sources/users collapses into one row (first one wins; earliest first-seen kept).
            migrationBuilder.Sql(@"
                INSERT INTO Jobs (Provider, ExternalId, Hash, Title, Company, Location, Url, Description,
                                  PostedUtc, FirstSeenUtc, Salary, EmploymentType)
                SELECT k.Provider, k.ExternalId, o.Hash, o.Title, o.Company, o.Location, o.Url, o.Description,
                       o.PostedUtc,
                       (SELECT MIN(o2.FirstSeenUtc)
                          FROM JobKeys k2 JOIN Jobs_old o2 ON o2.Id = k2.OldId
                         WHERE k2.Provider = k.Provider AND k2.ExternalId = k.ExternalId),
                       o.Salary, o.EmploymentType
                FROM JobKeys k
                JOIN Jobs_old o ON o.Id = k.OldId
                WHERE k.OldId = (SELECT MIN(k3.OldId) FROM JobKeys k3
                                  WHERE k3.Provider = k.Provider AND k3.ExternalId = k.ExternalId);");

            // Every old row becomes one delivery, carrying its Processed/Delivered state and its owner.
            migrationBuilder.Sql(@"
                INSERT INTO JobDeliveries (JobId, SourceId, OwnerChatId, CreatedUtc, Processed, Delivered)
                SELECT j.Id, o.SourceId, o.OwnerChatId, o.FirstSeenUtc, o.Processed, o.Delivered
                FROM Jobs_old o
                JOIN JobKeys k ON k.OldId = o.Id
                JOIN Jobs j ON j.Provider = k.Provider AND j.ExternalId = k.ExternalId;");

            migrationBuilder.Sql(@"DROP TABLE JobKeys;");
            migrationBuilder.Sql(@"DROP TABLE Jobs_old;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one Jobs row per (source, vacancy) with the per-user columns on it.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_Jobs_Provider_ExternalId;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_Jobs_Hash;");
            migrationBuilder.Sql(@"ALTER TABLE Jobs RENAME TO Jobs_shared;");

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Salary = table.Column<int>(type: "INTEGER", nullable: true),
                    EmploymentType = table.Column<string>(type: "TEXT", nullable: true),
                    Processed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(@"
                INSERT INTO Jobs (SourceId, OwnerChatId, ExternalId, Hash, Title, Company, Location, Url, Description,
                                  PostedUtc, FirstSeenUtc, Salary, EmploymentType, Processed, Delivered)
                SELECT d.SourceId, d.OwnerChatId, j.ExternalId, j.Hash, j.Title, j.Company, j.Location, j.Url, j.Description,
                       j.PostedUtc, d.CreatedUtc, j.Salary, j.EmploymentType, d.Processed, d.Delivered
                FROM JobDeliveries d
                JOIN Jobs_shared j ON j.Id = d.JobId;");

            migrationBuilder.DropTable(name: "JobDeliveries");
            migrationBuilder.Sql(@"DROP TABLE Jobs_shared;");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OwnerChatId_Hash",
                table: "Jobs",
                columns: new[] { "OwnerChatId", "Hash" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OwnerChatId_Processed",
                table: "Jobs",
                columns: new[] { "OwnerChatId", "Processed" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SourceId_ExternalId",
                table: "Jobs",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);
        }
    }
}