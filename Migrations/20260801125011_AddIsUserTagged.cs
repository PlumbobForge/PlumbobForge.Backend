using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddIsUserTagged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUserTagged",
                table: "MetaEntities",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUserTagged",
                table: "MetaEntities");
        }
    }
}
