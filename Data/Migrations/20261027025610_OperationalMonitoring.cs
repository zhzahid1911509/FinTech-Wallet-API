using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinTechWalletAPI.Data.Migrations
{
    /// <inheritdoc />
    public partial class OperationalMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BalanceMismatchCount = table.Column<int>(type: "int", nullable: true),
                    PostingMismatchCount = table.Column<int>(type: "int", nullable: true),
                    ReportJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationRun_Finished", "([Status] = 'Running' AND [FinishedAt] IS NULL) OR ([Status] <> 'Running' AND [FinishedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_ReconciliationRun_Status", "[Status] IN ('Running','Healthy','Unhealthy','Failed','Abandoned')");
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationSchedules", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationSchedule_Lease", "([LeaseId] IS NULL AND [LeaseUntil] IS NULL) OR ([LeaseId] IS NOT NULL AND [LeaseUntil] IS NOT NULL)");
                    table.CheckConstraint("CK_ReconciliationSchedule_Singleton", "[Id] = 1");
                });

            migrationBuilder.InsertData(
                table: "ReconciliationSchedules",
                columns: new[] { "Id", "LeaseId", "LeaseUntil", "NextRunAt" },
                values: new object[] { 1, null, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_CreatedAt",
                table: "AuditEvents",
                columns: new[] { "Action", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorId_CreatedAt",
                table: "AuditEvents",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CreatedAt_Id",
                table: "AuditEvents",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_StartedAt_Id",
                table: "ReconciliationRuns",
                columns: new[] { "StartedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationRuns");

            migrationBuilder.DropTable(
                name: "ReconciliationSchedules");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Action_CreatedAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ActorId_CreatedAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_CreatedAt_Id",
                table: "AuditEvents");
        }
    }
}
