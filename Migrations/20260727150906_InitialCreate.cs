using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlumbobForge.Backend.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Default = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigEntities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SetsEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    LongName = table.Column<string>(type: "TEXT", nullable: false),
                    IsLegacy = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsExpanded = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Dirty = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ParentSetsEntityId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetsEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SetsEntities_SetsEntities_ParentSetsEntityId",
                        column: x => x.ParentSetsEntityId,
                        principalTable: "SetsEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SettingEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigEntityId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettingEntities_ConfigEntities_ConfigEntityId",
                        column: x => x.ConfigEntityId,
                        principalTable: "ConfigEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigSetsEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConfigEntityId = table.Column<long>(type: "INTEGER", nullable: false),
                    SetsEntityId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigSetsEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigSetsEntities_ConfigEntities_ConfigEntityId",
                        column: x => x.ConfigEntityId,
                        principalTable: "ConfigEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigSetsEntities_SetsEntities_SetsEntityId",
                        column: x => x.SetsEntityId,
                        principalTable: "SetsEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetaEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    CompleteFileName = table.Column<string>(type: "TEXT", nullable: false),
                    FileType = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<double>(type: "REAL", nullable: false),
                    Filehash = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    URL = table.Column<string>(type: "TEXT", nullable: true),
                    PackageType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceID = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbnailID = table.Column<string>(type: "TEXT", nullable: true),
                    InstallDate = table.Column<string>(type: "TEXT", nullable: true),
                    Manifest = table.Column<string>(type: "TEXT", nullable: true),
                    CASCategories = table.Column<string>(type: "TEXT", nullable: true),
                    SetsEntityId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaEntities_SetsEntities_SetsEntityId",
                        column: x => x.SetsEntityId,
                        principalTable: "SetsEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSetsEntities_ConfigEntityId",
                table: "ConfigSetsEntities",
                column: "ConfigEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSetsEntities_SetsEntityId",
                table: "ConfigSetsEntities",
                column: "SetsEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaEntities_SetsEntityId",
                table: "MetaEntities",
                column: "SetsEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SetsEntities_ParentSetsEntityId",
                table: "SetsEntities",
                column: "ParentSetsEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SettingEntities_ConfigEntityId",
                table: "SettingEntities",
                column: "ConfigEntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigSetsEntities");

            migrationBuilder.DropTable(
                name: "MetaEntities");

            migrationBuilder.DropTable(
                name: "SettingEntities");

            migrationBuilder.DropTable(
                name: "SetsEntities");

            migrationBuilder.DropTable(
                name: "ConfigEntities");
        }
    }
}
