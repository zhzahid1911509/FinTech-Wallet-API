using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinTechWalletAPI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RiskBasedTransferAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransferAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedByTransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferAuthorizations", x => x.Id);
                    table.CheckConstraint("CK_TransferAuthorization_Expiry", "[ExpiresAt] > [CreatedAt]");
                    table.ForeignKey(
                        name: "FK_TransferAuthorizations_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferAuthorizations_Transfers_ConsumedByTransferId",
                        column: x => x.ConsumedByTransferId,
                        principalTable: "Transfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferAuthorizations_ConsumedByTransferId",
                table: "TransferAuthorizations",
                column: "ConsumedByTransferId",
                unique: true,
                filter: "[ConsumedByTransferId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransferAuthorizations_SessionId_ExpiresAt",
                table: "TransferAuthorizations",
                columns: new[] { "SessionId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferAuthorizations");
        }
    }
}
