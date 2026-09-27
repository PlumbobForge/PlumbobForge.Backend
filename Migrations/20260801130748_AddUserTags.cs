using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserTags",
                table: "MetaEntities",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserTags",
                table: "MetaEntities");
        }
    }
}
