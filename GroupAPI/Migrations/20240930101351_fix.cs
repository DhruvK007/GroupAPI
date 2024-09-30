using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupAPI.Migrations
{
    /// <inheritdoc />
    public partial class fix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JoinRequest_Groups_GroupId",
                table: "JoinRequest");

            migrationBuilder.DropForeignKey(
                name: "FK_JoinRequest_Users_UserId",
                table: "JoinRequest");

            migrationBuilder.DropPrimaryKey(
                name: "PK_JoinRequest",
                table: "JoinRequest");

            migrationBuilder.RenameTable(
                name: "JoinRequest",
                newName: "JoinRequests");

            migrationBuilder.RenameIndex(
                name: "IX_JoinRequest_UserId",
                table: "JoinRequests",
                newName: "IX_JoinRequests_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_JoinRequest_GroupId",
                table: "JoinRequests",
                newName: "IX_JoinRequests_GroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_JoinRequests",
                table: "JoinRequests",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_JoinRequests_Groups_GroupId",
                table: "JoinRequests",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JoinRequests_Users_UserId",
                table: "JoinRequests",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JoinRequests_Groups_GroupId",
                table: "JoinRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_JoinRequests_Users_UserId",
                table: "JoinRequests");

            migrationBuilder.DropPrimaryKey(
                name: "PK_JoinRequests",
                table: "JoinRequests");

            migrationBuilder.RenameTable(
                name: "JoinRequests",
                newName: "JoinRequest");

            migrationBuilder.RenameIndex(
                name: "IX_JoinRequests_UserId",
                table: "JoinRequest",
                newName: "IX_JoinRequest_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_JoinRequests_GroupId",
                table: "JoinRequest",
                newName: "IX_JoinRequest_GroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_JoinRequest",
                table: "JoinRequest",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_JoinRequest_Groups_GroupId",
                table: "JoinRequest",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JoinRequest_Users_UserId",
                table: "JoinRequest",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
