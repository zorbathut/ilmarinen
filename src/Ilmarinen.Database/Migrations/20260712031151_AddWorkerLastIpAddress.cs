using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerLastIpAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastIpAddress",
                table: "Workers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastIpAddress",
                table: "Workers");
        }
    }
}
