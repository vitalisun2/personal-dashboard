using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class PlatformSyncInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "entity_change_cursor",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_change_cursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "entity_change_journal",
                schema: "platform",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_change_journal", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "sync_operation_results",
                schema: "platform",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_operation_results", x => x.OperationId);
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "entity_change_cursor",
                columns: new[] { "Id", "Sequence" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_entity_change_journal_Type_Id_Version",
                schema: "platform",
                table: "entity_change_journal",
                columns: new[] { "Type", "Id", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_change_cursor",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "entity_change_journal",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "sync_operation_results",
                schema: "platform");
        }
    }
}
