using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is WorkerPriority.Medium (1), not the scaffolder's 0 — 0 is Low, which would silently demote every existing worker. It only ever applies to this backfill: the model declares no default, so EF always writes the column explicitly.
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Workers",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Workers");
        }
    }
}
