using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FirstSeen",
                table: "Workers",
                newName: "RegisteredAt");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Workers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "PublicKey",
                table: "Workers",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_Name",
                table: "Workers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workers_Name",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "Workers");

            migrationBuilder.RenameColumn(
                name: "RegisteredAt",
                table: "Workers",
                newName: "FirstSeen");
        }
    }
}
