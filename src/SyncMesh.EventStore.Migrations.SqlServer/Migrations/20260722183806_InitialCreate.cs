using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncMesh.EventStore.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    GlobalEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamVersion = table.Column<long>(type: "bigint", nullable: false),
                    OriginSiteId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    HlcPhysicalTicks = table.Column<long>(type: "bigint", nullable: false),
                    HlcLogicalCounter = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.GlobalEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_HlcPhysicalTicks_HlcLogicalCounter",
                table: "Events",
                columns: new[] { "HlcPhysicalTicks", "HlcLogicalCounter" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_StreamId_StreamVersion",
                table: "Events",
                columns: new[] { "StreamId", "StreamVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
