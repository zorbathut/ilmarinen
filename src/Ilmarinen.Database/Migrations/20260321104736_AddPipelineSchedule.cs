using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTriggeredAt",
                table: "Pipelines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "Pipelines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTriggeredAt",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "Pipelines");
        }
    }
}
