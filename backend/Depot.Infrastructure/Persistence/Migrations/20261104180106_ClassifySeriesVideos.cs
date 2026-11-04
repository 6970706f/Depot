using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Depot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassifySeriesVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtrasFolder",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "OrganizationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "Main");

            migrationBuilder.AddColumn<int>(
                name: "PlacementSeason",
                table: "OrganizationJobs",
                type: "INTEGER",
                nullable: true);

            // Jobs found before this version keep going where they went: the chosen season.
            migrationBuilder.Sql("UPDATE OrganizationJobs SET PlacementSeason = Season;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtrasFolder",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "OrganizationJobs");

            migrationBuilder.DropColumn(
                name: "PlacementSeason",
                table: "OrganizationJobs");
        }
    }
}
