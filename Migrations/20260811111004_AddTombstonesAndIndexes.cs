using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTombstonesAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tombstones",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    PackageType = table.Column<string>(type: "TEXT", nullable: false),
                    CASCategories = table.Column<string>(type: "TEXT", nullable: true),
                    CASAge = table.Column<string>(type: "TEXT", nullable: true),
                    CASGender = table.Column<string>(type: "TEXT", nullable: true),
                    CASOutfitCategory = table.Column<string>(type: "TEXT", nullable: true),
                    IsUserTagged = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserTags = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tombstones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaEntities_Enabled",
                table: "MetaEntities",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_MetaEntities_FileName",
                table: "MetaEntities",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_MetaEntities_PackageType",
                table: "MetaEntities",
                column: "PackageType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tombstones");

            migrationBuilder.DropIndex(
                name: "IX_MetaEntities_Enabled",
                table: "MetaEntities");

            migrationBuilder.DropIndex(
                name: "IX_MetaEntities_FileName",
                table: "MetaEntities");

            migrationBuilder.DropIndex(
                name: "IX_MetaEntities_PackageType",
                table: "MetaEntities");
        }
    }
}
