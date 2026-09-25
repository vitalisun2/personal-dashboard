using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class ModulesInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.EnsureSchema(
                name: "planning");

            migrationBuilder.EnsureSchema(
                name: "tasks");

            migrationBuilder.CreateTable(
                name: "chat_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_nodes",
                schema: "knowledge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Markdown = table.Column<string>(type: "text", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "planning_projects",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "planning_tombstones",
                schema: "planning",
                columns: table => new
                {
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_tombstones", x => new { x.Type, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "task_sections",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Location = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_sections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_tombstones",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_tombstones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chat_turns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserText = table.Column<string>(type: "text", nullable: false),
                    AssistantText = table.Column<string>(type: "text", nullable: false),
                    ScopeMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityVersion = table.Column<long>(type: "bigint", nullable: true),
                    RequestedModel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActualModel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelRoute = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FallbackReason = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_turns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chat_turns_chat_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "chat_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_items",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ItemKind = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: true),
                    MilestoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_items_planning_items_MilestoneId",
                        column: x => x.MilestoneId,
                        principalSchema: "planning",
                        principalTable: "planning_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planning_items_planning_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "planning",
                        principalTable: "planning_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_items",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    MilestoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: true),
                    Location = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_items_task_sections_SectionId",
                        column: x => x.SectionId,
                        principalSchema: "tasks",
                        principalTable: "task_sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConfirmationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_proposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chat_proposals_chat_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "chat_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_proposals_chat_turns_TurnId",
                        column: x => x.TurnId,
                        principalTable: "chat_turns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_UpdatedAtUtc",
                table: "chat_conversations",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_chat_proposals_ConversationId_State_CreatedAtUtc",
                table: "chat_proposals",
                columns: new[] { "ConversationId", "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_proposals_TurnId",
                table: "chat_proposals",
                column: "TurnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_ConversationId_CreatedAtUtc_Id",
                table: "chat_turns",
                columns: new[] { "ConversationId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_nodes_ArchivedAt",
                schema: "knowledge",
                table: "knowledge_nodes",
                column: "ArchivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_nodes_DeletedAt",
                schema: "knowledge",
                table: "knowledge_nodes",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_nodes_ParentId_Position",
                schema: "knowledge",
                table: "knowledge_nodes",
                columns: new[] { "ParentId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_planning_items_MilestoneId",
                schema: "planning",
                table: "planning_items",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_planning_items_ProjectId",
                schema: "planning",
                table: "planning_items",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_task_items_Location_SectionId_Position",
                schema: "tasks",
                table: "task_items",
                columns: new[] { "Location", "SectionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_task_items_ProjectId_MilestoneId_FeatureId",
                schema: "tasks",
                table: "task_items",
                columns: new[] { "ProjectId", "MilestoneId", "FeatureId" });

            migrationBuilder.CreateIndex(
                name: "IX_task_items_SectionId",
                schema: "tasks",
                table: "task_items",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_task_sections_Location_Name",
                schema: "tasks",
                table: "task_sections",
                columns: new[] { "Location", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_proposals");

            migrationBuilder.DropTable(
                name: "knowledge_nodes",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "planning_items",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "planning_tombstones",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "task_items",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "task_tombstones",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "chat_turns");

            migrationBuilder.DropTable(
                name: "planning_projects",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "task_sections",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "chat_conversations");
        }
    }
}
