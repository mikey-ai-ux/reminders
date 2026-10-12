using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Reminders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTierPlansAndChannelQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SmsMonthlyLimit",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StripePriceId",
                table: "SubscriptionPlans",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VoiceMonthlyLimit",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionPlanId",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "SmsMonthlyLimit", "StripePriceId", "VoiceMonthlyLimit" },
                values: new object[] { 0, null, 0 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "Name", "SmsMonthlyLimit", "StripePriceId", "VoiceMonthlyLimit" },
                values: new object[] { "Starter", 100, null, 10 });

            migrationBuilder.InsertData(
                table: "SubscriptionPlans",
                columns: new[] { "Id", "AllowedChannelsJson", "BillingInterval", "FreeNotificationQuota", "Name", "Price", "SmsMonthlyLimit", "StripePriceId", "VoiceMonthlyLimit" },
                values: new object[,]
                {
                    { 3, "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", "Monthly", 1000, "Growth", 29.99m, 2000, null, 200 },
                    { 4, "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", "Monthly", 1000, "Scale", 99.99m, 10000, null, 1000 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_SubscriptionPlanId",
                table: "AspNetUsers",
                column: "SubscriptionPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_SubscriptionPlans_SubscriptionPlanId",
                table: "AspNetUsers",
                column: "SubscriptionPlanId",
                principalTable: "SubscriptionPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_SubscriptionPlans_SubscriptionPlanId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_SubscriptionPlanId",
                table: "AspNetUsers");

            migrationBuilder.DeleteData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DropColumn(
                name: "SmsMonthlyLimit",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "StripePriceId",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "VoiceMonthlyLimit",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "SubscriptionPlanId",
                table: "AspNetUsers");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                column: "Name",
                value: "Pro");
        }
    }
}
