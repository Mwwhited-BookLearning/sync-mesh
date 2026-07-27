using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncMesh.EventStore.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEventLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventLineage",
                columns: table => new
                {
                    ChildEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentEventId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLineage", x => new { x.ChildEventId, x.ParentEventId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventLineage_ParentEventId",
                table: "EventLineage",
                column: "ParentEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventLineage");
        }
    }
}
