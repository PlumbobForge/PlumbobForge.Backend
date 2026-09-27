using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSetsEntityIdToTombstone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SetsEntityId",
                table: "Tombstones",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetsEntityId",
                table: "Tombstones");
        }
    }
}
