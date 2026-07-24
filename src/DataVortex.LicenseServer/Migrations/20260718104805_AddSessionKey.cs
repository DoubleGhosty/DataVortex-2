using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataVortex.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SessionKey",
                table: "Sessions",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionKey",
                table: "Sessions");
        }
    }
}
