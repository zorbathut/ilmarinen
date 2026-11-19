using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddJobMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobMetricsBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobMetricsBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobMetricsBatches_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobSteps",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Image = table.Column<string>(type: "text", nullable: true),
                    ImageFromEarlierStep = table.Column<bool>(type: "boolean", nullable: false),
                    PullSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CpuSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CpuThrottledSeconds = table.Column<double>(type: "double precision", nullable: false),
                    PeakCores = table.Column<double>(type: "double precision", nullable: false),
                    PeakMemBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReadBytes = table.Column<long>(type: "bigint", nullable: false),
                    WriteBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSteps", x => new { x.JobId, x.Index });
                    table.ForeignKey(
                        name: "FK_JobSteps_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobMetricsBatches_JobId_SequenceNumber",
                table: "JobMetricsBatches",
                columns: new[] { "JobId", "SequenceNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobMetricsBatches");

            migrationBuilder.DropTable(
                name: "JobSteps");
        }
    }
}
