using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineNotificationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotificationPolicy",
                table: "Pipelines",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationPolicy",
                table: "Pipelines");
        }
    }
}
