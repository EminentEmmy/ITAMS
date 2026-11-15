using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationsSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NotificationsSeenUtc",
                table: "Users",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationsSeenUtc",
                table: "Users");
        }
    }
}
