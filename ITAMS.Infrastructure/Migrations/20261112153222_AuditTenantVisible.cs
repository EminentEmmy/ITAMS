using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditTenantVisible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TenantVisible",
                table: "SecurityAuditLogs",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantVisible",
                table: "SecurityAuditLogs");
        }
    }
}
