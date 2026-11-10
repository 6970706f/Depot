using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Depot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJellyfinLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JellyfinSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JellyfinSettings", x => x.Id);
                    table.CheckConstraint("CK_JellyfinSettings_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "LibrarySyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentLibrary = table.Column<string>(type: "TEXT", nullable: true),
                    LibrariesTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    LibrariesCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    LibrariesFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsSeen = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsRemoved = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Errors = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibrarySyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaLibraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Locations = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaLibraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    SortName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    ProductionYear = table.Column<int>(type: "INTEGER", nullable: true),
                    PremiereDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OfficialRating = table.Column<string>(type: "TEXT", nullable: true),
                    CommunityRating = table.Column<double>(type: "REAL", nullable: true),
                    RuntimeTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: false),
                    Studios = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderIds = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesStatus = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    SeasonExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    IndexNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexNumberEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentIndexNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PrimaryImageTag = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbImageTag = table.Column<string>(type: "TEXT", nullable: true),
                    LogoImageTag = table.Column<string>(type: "TEXT", nullable: true),
                    BackdropImageTags = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaItems_MediaLibraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "MediaLibraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Container = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Bitrate = table.Column<long>(type: "INTEGER", nullable: true),
                    RuntimeTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Streams = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibrarySyncRuns_Status",
                table: "LibrarySyncRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_MediaItemId_ExternalId",
                table: "MediaFiles",
                columns: new[] { "MediaItemId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ExternalId",
                table: "MediaItems",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_LibraryId_Kind",
                table: "MediaItems",
                columns: new[] { "LibraryId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_SeasonExternalId",
                table: "MediaItems",
                column: "SeasonExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_SeriesExternalId",
                table: "MediaItems",
                column: "SeriesExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaLibraries_ExternalId",
                table: "MediaLibraries",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JellyfinSettings");

            migrationBuilder.DropTable(
                name: "LibrarySyncRuns");

            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropTable(
                name: "MediaItems");

            migrationBuilder.DropTable(
                name: "MediaLibraries");
        }
    }
}
