using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Depot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TorrentHash = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentName = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    SubtitlesJson = table.Column<string>(type: "TEXT", nullable: false),
                    NotesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationPath = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedWith = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedByUser = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationPathMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    QbittorrentPath = table.Column<string>(type: "TEXT", nullable: false),
                    DepotPath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationPathMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    MoviesPath = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesPath = table.Column<string>(type: "TEXT", nullable: false),
                    ImportMode = table.Column<string>(type: "TEXT", nullable: false),
                    AutoOrganize = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSettings", x => x.Id);
                    table.CheckConstraint("CK_OrganizationSettings_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationTorrents",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VideoCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationTorrents", x => x.Hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationJobs_Status",
                table: "OrganizationJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationJobs_TorrentHash_RelativePath",
                table: "OrganizationJobs",
                columns: new[] { "TorrentHash", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationPathMappings_QbittorrentPath",
                table: "OrganizationPathMappings",
                column: "QbittorrentPath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationJobs");

            migrationBuilder.DropTable(
                name: "OrganizationPathMappings");

            migrationBuilder.DropTable(
                name: "OrganizationSettings");

            migrationBuilder.DropTable(
                name: "OrganizationTorrents");
        }
    }
}
