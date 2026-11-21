using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportShop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequireReceivedAmountForCollectedPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            const string validPayment = """
                ([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded')
                    AND [ReceivedAmount] IS NOT NULL AND [ReceivedAmount] = [Amount] AND [PaidAt] IS NOT NULL)
                OR ([PaymentStatus] IN ('Pending', 'Failed', 'Expired')
                    AND [ReceivedAmount] IS NULL AND [PaidAt] IS NULL)
                """;

            // Keep payment writers out between the preflight and the trusted replacement.
            // All statements participate in EF Core's migration transaction.
            migrationBuilder.Sql($"""
                SET XACT_ABORT ON;
                IF EXISTS (SELECT 1 FROM [dbo].[Payments] WITH (TABLOCKX, HOLDLOCK)
                           WHERE NOT ({validPayment}))
                    THROW 51041, 'Payments contain invalid ReceivedAmount or PaidAt for PaymentStatus. Reconcile the affected payments before retrying this migration.', 1;

                ALTER TABLE [dbo].[Payments] DROP CONSTRAINT [CK_Payments_Status_ReceivedAmount_PaidAt];
                ALTER TABLE [dbo].[Payments] WITH CHECK ADD CONSTRAINT [CK_Payments_Status_ReceivedAmount_PaidAt]
                    CHECK ({validPayment});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Status_ReceivedAmount_PaidAt",
                table: "Payments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Status_ReceivedAmount_PaidAt",
                table: "Payments",
                sql: "([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded') AND [ReceivedAmount] = [Amount] AND [PaidAt] IS NOT NULL) OR ([PaymentStatus] IN ('Pending', 'Failed', 'Expired') AND [ReceivedAmount] IS NULL AND [PaidAt] IS NULL)");
        }
    }
}
