﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using GroupAPI.Data;
using GroupAPI.Models;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;

namespace GroupAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GroupController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public GroupController(ApplicationDbContext context)
        {
            _context = context;
        }

        // POST: api/Group/Create
        [HttpPost("Create")]
        public async Task<ActionResult<GroupResponseModel>> CreateGroup(CreateGroupModel model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
            {
                return NotFound("User not found");
            }

            var group = new Group
            {
                Name = model.Name,
                Description = model.Description,
                Photo = model.Photo,
                Code = GenerateUniqueCode(),
                CreatorId = userId
            };

            _context.Groups.Add(group);
            await _context.SaveChangesAsync();

            // Add the creator as a member
            var groupMember = new GroupMember
            {
                UserId = userId,
                GroupId = group.Id
            };
            _context.GroupMembers.Add(groupMember);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetGroup), new { id = group.Id }, new GroupResponseModel(group));
        }

        // GET: api/Group/{id}
        [HttpGet("{id}")]
        public async Task<ActionResult<GroupResponseModel>> GetGroup(string id)
        {
            var group = await _context.Groups.FindAsync(id);

            if (group == null)
            {
                return NotFound();
            }

            return new GroupResponseModel(group);
        }

        // POST: api/Group/JoinRequest
        [HttpPost("JoinRequest")]
        public async Task<IActionResult> CreateJoinRequest(JoinRequestModel model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Code == model.GroupCode);

            if (group == null)
            {
                return NotFound("Group not found");
            }

            // Check if the user is already a member or creator of the group
            var isMember = await _context.GroupMembers.AnyAsync(gm => gm.GroupId == group.Id && gm.UserId == userId);
            var isCreator = group.CreatorId == userId;

            if (isMember || isCreator)
            {
                return BadRequest("You are already a member or creator of this group");
            }

            // Check if a join request already exists
            var existingRequest = await _context.JoinRequests.FirstOrDefaultAsync(jr => jr.GroupId == group.Id && jr.UserId == userId);
            if (existingRequest != null)
            {
                return BadRequest("You have already sent a join request for this group");
            }

            var joinRequest = new JoinRequest
            {
                UserId = userId,
                GroupId = group.Id,
                Status = JoinRequestStatus.Pending
            };

            _context.JoinRequests.Add(joinRequest);
            await _context.SaveChangesAsync();

            return Ok("Join request sent successfully");
        }

        // POST: api/Group/RespondToJoinRequest
        [HttpPost("RespondToJoinRequest")]
        public async Task<IActionResult> RespondToJoinRequest(RespondToJoinRequestModel model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var joinRequest = await _context.JoinRequests.Include(jr => jr.Group).FirstOrDefaultAsync(jr => jr.Id == model.JoinRequestId);

            if (joinRequest == null)
            {
                return NotFound("Join request not found");
            }

            if (joinRequest.Group.CreatorId != userId)
            {
                return Forbid("Only the group creator can respond to join requests");
            }

            if (model.Accept)
            {
                var groupMember = new GroupMember
                {
                    UserId = joinRequest.UserId,
                    GroupId = joinRequest.GroupId
                };
                _context.GroupMembers.Add(groupMember);
            }

            _context.JoinRequests.Remove(joinRequest);
            await _context.SaveChangesAsync();

            return Ok(model.Accept ? "Join request accepted" : "Join request rejected");
        }


        [HttpPost("Leave")]
        public async Task<IActionResult> LeaveGroup([FromBody] LeaveGroupModel model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Unauthorized("User is not authenticated");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var group = await _context.Groups
                    .Include(g => g.Members)  // Use the proper navigation property
                    .FirstOrDefaultAsync(g => g.Id == model.GroupId);

                if (group == null)
                {
                    return NotFound("Group not found");
                }

                var groupMember = group.Members.FirstOrDefault(gm => gm.UserId == userId);
                if (groupMember == null)
                {
                    return BadRequest("You are not a member of this group");
                }

                if (group.CreatorId == userId)
                {
                    return BadRequest("As the group creator, you cannot leave the group. You must delete it.");
                }

                // Check for outstanding debts (same as before)
                var amountOwed = await _context.ExpenseSplits
                    .Where(es => es.UserId == userId && es.Expense.GroupId == group.Id && es.IsPaid == SplitStatus.Unpaid)
                    .SumAsync(es => es.Amount);

                var amountToReceive = await _context.GroupExpenses
                    .Where(ge => ge.PaidById == userId && ge.GroupId == group.Id && ge.Status != ExpenseStatus.Settled)
                    .SumAsync(ge => ge.Amount);

                if (amountOwed > 0 || amountToReceive > 0)
                {
                    string errorMessage = "Cannot leave the group. ";
                    if (amountOwed > 0) errorMessage += $"You owe {amountOwed:C}. ";
                    if (amountToReceive > 0) errorMessage += $"You are owed {amountToReceive:C}. ";
                    errorMessage += "Please settle all debts before leaving.";
                    return BadRequest(errorMessage);
                }

                _context.GroupMembers.Remove(groupMember);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return Ok("You have successfully left the group.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"An error occurred while leaving the group: {ex.Message}");
            }
        }


        // DELETE: api/Group/Delete
        public async Task<IActionResult> DeleteGroup(string groupId, string userId)
        {
            // Load the group with all its related entities
            var group = await _context.Groups
                .Include(g => g.Expenses)
                    .ThenInclude(e => e.Splits)
                        .ThenInclude(s => s.Payments) // Include payments related to splits
                .Include(g => g.Members) // Include group members
                .Include(g => g.JoinRequests) // Include join requests
                .FirstOrDefaultAsync(g => g.Id == groupId);

            if (group == null)
            {
                return NotFound();
            }

            // Check if the user has pending payments (either owes money or is owed money)
            var pendingSplits = group.Expenses
                .SelectMany(e => e.Splits)
                .Where(s => (s.UserId == userId || s.Expense.PaidById == userId) && s.IsPaid != SplitStatus.Paid)
                .ToList();

            if (pendingSplits.Any())
            {
                return BadRequest("Cannot delete the group or leave as there are pending payments.");
            }

            // If no pending payments, proceed with deletion

            // Remove related payments
            foreach (var expense in group.Expenses)
            {
                foreach (var split in expense.Splits)
                {
                    _context.Payments.RemoveRange(split.Payments);
                }

                // Remove splits for each expense
                _context.ExpenseSplits.RemoveRange(expense.Splits);
            }

            // Remove group expenses
            _context.GroupExpenses.RemoveRange(group.Expenses);

            // Remove group members
            _context.GroupMembers.RemoveRange(group.Members);

            // Remove join requests
            _context.JoinRequests.RemoveRange(group.JoinRequests);

            // Finally, remove the group
            _context.Groups.Remove(group);

            // Save changes to the database
            await _context.SaveChangesAsync();

            return Ok();
        }




        private string GenerateUniqueCode()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var code = new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());

            // Ensure the code is unique
            while (_context.Groups.Any(g => g.Code == code))
            {
                code = new string(Enumerable.Repeat(chars, 6)
                    .Select(s => s[random.Next(s.Length)]).ToArray());
            }

            return code;
        }

        public class CreateGroupModel
        {
            [Required]
            public string Name { get; set; }
            public string Description { get; set; }
            public string Photo { get; set; }
        }

        public class GroupResponseModel
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Photo { get; set; }
            public string Code { get; set; }
            public string CreatorId { get; set; }

            public GroupResponseModel(Group group)
            {
                Id = group.Id;
                Name = group.Name;
                Description = group.Description;
                Photo = group.Photo;
                Code = group.Code;
                CreatorId = group.CreatorId;
            }
        }

        public class JoinRequestModel
        {
            [Required]
            public string GroupCode { get; set; }
        }

        public class RespondToJoinRequestModel
        {
            [Required]
            public string JoinRequestId { get; set; }
            [Required]
            public bool Accept { get; set; }
        }
        public class LeaveGroupModel
        {
            [Required]
            public string GroupId { get; set; }
        }

        public class DeleteGroupModel
        {
            [Required]
            public string GroupId { get; set; }
        }
    }
}