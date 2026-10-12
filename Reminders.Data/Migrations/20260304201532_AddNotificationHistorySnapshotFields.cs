using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reminders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationHistorySnapshotFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MessageSnapshot",
                table: "ReminderNotifications",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReminderName",
                table: "ReminderNotifications",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledForUtc",
                table: "ReminderNotifications",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "ReminderNotifications",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageSnapshot",
                table: "ReminderNotifications");

            migrationBuilder.DropColumn(
                name: "ReminderName",
                table: "ReminderNotifications");

            migrationBuilder.DropColumn(
                name: "ScheduledForUtc",
                table: "ReminderNotifications");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "ReminderNotifications");
        }
    }
}
