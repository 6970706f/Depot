using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Depot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaLibraryDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAnime",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MemberIds",
                table: "MediaItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "People",
                table: "MediaItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ProductionLocations",
                table: "MediaItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "MediaItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Kind_IsAnime",
                table: "MediaItems",
                columns: new[] { "Kind", "IsAnime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Kind_IsAnime",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "IsAnime",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "MemberIds",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "People",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ProductionLocations",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "MediaItems");
        }
    }
}
