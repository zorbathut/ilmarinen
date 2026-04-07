using System;
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveWorkerCurrentJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentJobId",
                table: "Workers");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_WorkerId_Status",
                table: "Jobs",
                columns: new[] { "WorkerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_WorkerId_Status",
                table: "Jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentJobId",
                table: "Workers",
                type: "uuid",
                nullable: true);
        }
    }
}
