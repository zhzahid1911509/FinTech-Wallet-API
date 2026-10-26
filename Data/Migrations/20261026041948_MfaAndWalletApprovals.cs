using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinTechWalletAPI.Data.Migrations
{
    /// <inheritdoc />
    public partial class MfaAndWalletApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StateVersion",
                table: "Wallets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethod",
                table: "Sessions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaVerifiedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MfaProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EnrollmentExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAcceptedStep = table.Column<long>(type: "bigint", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_MfaProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MfaRecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MfaRecoveryCodes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalletApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WalletStateVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterSecurityStamp = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ReviewedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletApprovals", x => x.Id);
                    table.CheckConstraint("CK_Approval_SeparateReviewer", "[Status] <> 'Approved' OR ([ReviewedBy] IS NOT NULL AND [ReviewedBy] <> [RequestedBy])");
                    table.CheckConstraint("CK_Approval_Status", "[Status] IN ('Pending','Approved','Rejected','Cancelled','Expired','Invalidated')");
                    table.ForeignKey(
                        name: "FK_WalletApprovals_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletApprovals_AspNetUsers_ReviewedBy",
                        column: x => x.ReviewedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletApprovals_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MfaRecoveryCodes_UserId_Hash",
                table: "MfaRecoveryCodes",
                columns: new[] { "UserId", "Hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletApprovals_RequestedBy",
                table: "WalletApprovals",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_WalletApprovals_ReviewedBy",
                table: "WalletApprovals",
                column: "ReviewedBy");

            migrationBuilder.CreateIndex(
                name: "IX_WalletApprovals_WalletId",
                table: "WalletApprovals",
                column: "WalletId",
                unique: true,
                filter: "[Status] = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MfaProfiles");

            migrationBuilder.DropTable(
                name: "MfaRecoveryCodes");

            migrationBuilder.DropTable(
                name: "WalletApprovals");

            migrationBuilder.DropColumn(
                name: "StateVersion",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethod",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "MfaVerifiedAt",
                table: "Sessions");
        }
    }
}
