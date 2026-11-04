using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Depot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChooseLibraryWhenAdding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Jobs created by the old file name classification that were never imported have
            // no library choice behind them, so they go. Imported ones stay as history.
            migrationBuilder.Sql(
                "DELETE FROM \"OrganizationJobs\" WHERE \"Status\" <> 'Completed' OR \"MediaType\" NOT IN ('Movie', 'Series') OR \"Title\" IS NULL;");

            migrationBuilder.DropTable(
                name: "OrganizationTorrents");

            migrationBuilder.DropColumn(
                name: "AutoOrganize",
                table: "OrganizationSettings");

            migrationBuilder.DropColumn(
                name: "Episode",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "NotesJson",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "RequestedByUser",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "SubtitlesJson",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "TmdbId",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "OrganizationJobs");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryTargets",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VideoCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryTargets", x => x.Hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryTargets_ScannedAt",
                table: "LibraryTargets",
                column: "ScannedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryTargets");

            migrationBuilder.AddColumn<bool>(
                name: "AutoOrganize",
                table: "OrganizationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "Episode",
                table: "OrganizationJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotesJson",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RequestedByUser",
                table: "OrganizationJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SubtitlesJson",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TmdbId",
                table: "OrganizationJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "OrganizationJobs",
                type: "INTEGER",
                nullable: true);

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
        }
    }
}
