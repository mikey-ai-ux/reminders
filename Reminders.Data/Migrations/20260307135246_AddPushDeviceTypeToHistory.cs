using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reminders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDeviceTypeToHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "ReminderNotifications",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "PushSubscriptions",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "ReminderNotifications");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "PushSubscriptions");
        }
    }
}
