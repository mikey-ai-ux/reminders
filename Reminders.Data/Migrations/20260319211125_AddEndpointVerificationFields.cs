using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reminders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEndpointVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationCode",
                table: "UserContactEndpoints",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerificationExpiresAtUtc",
                table: "UserContactEndpoints",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationToken",
                table: "UserContactEndpoints",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationCode",
                table: "UserContactEndpoints");

            migrationBuilder.DropColumn(
                name: "VerificationExpiresAtUtc",
                table: "UserContactEndpoints");

            migrationBuilder.DropColumn(
                name: "VerificationToken",
                table: "UserContactEndpoints");
        }
    }
}
