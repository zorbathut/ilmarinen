using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "Workers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // A paused worker stays paused (WorkerMode.Paused is 1).
            migrationBuilder.Sql("UPDATE \"Workers\" SET \"Mode\" = 1 WHERE \"Paused\"");

            migrationBuilder.DropColumn(
                name: "Paused",
                table: "Workers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Paused",
                table: "Workers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Only Paused survives the trip back; any other mode becomes unpaused.
            migrationBuilder.Sql("UPDATE \"Workers\" SET \"Paused\" = TRUE WHERE \"Mode\" = 1");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Workers");
        }
    }
}
