using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCASPDetailsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CASAge",
                table: "MetaEntities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CASGender",
                table: "MetaEntities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CASOutfitCategory",
                table: "MetaEntities",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CASAge",
                table: "MetaEntities");

            migrationBuilder.DropColumn(
                name: "CASGender",
                table: "MetaEntities");

            migrationBuilder.DropColumn(
                name: "CASOutfitCategory",
                table: "MetaEntities");
        }
    }
}
