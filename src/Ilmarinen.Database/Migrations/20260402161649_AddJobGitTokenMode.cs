using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddJobGitTokenMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GitTokenMode",
                table: "Jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitTokenMode",
                table: "Jobs");
        }
    }
}
