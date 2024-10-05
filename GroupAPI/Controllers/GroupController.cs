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


        // DELETE: api/Group/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteGroup(string id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Unauthorized();
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Load the group with all its related entities
                var group = await _context.Groups
                    .Include(g => g.Expenses)
                        .ThenInclude(e => e.Splits)
                            .ThenInclude(s => s.Payments)
                    .Include(g => g.Members)
                    .Include(g => g.JoinRequests)
                    .FirstOrDefaultAsync(g => g.Id == id);

                if (group == null)
                {
                    return NotFound("Group not found");
                }

                // Verify that the user is the creator of the group
                if (group.CreatorId != userId)
                {
                    return Forbid("Only the group creator can delete the group");
                }

                // Check for any pending payments in the group
                var pendingPayments = group.Expenses
                    .SelectMany(e => e.Splits)
                    .Any(s => s.IsPaid != SplitStatus.Paid);

                if (pendingPayments)
                {
                    return BadRequest("Cannot delete the group as there are pending payments. All payments must be settled first.");
                }

                // Delete related entities in the correct order to maintain referential integrity
                var payments = group.Expenses
                    .SelectMany(e => e.Splits)
                    .SelectMany(s => s.Payments);
                _context.Payments.RemoveRange(payments);

                var splits = group.Expenses
                    .SelectMany(e => e.Splits);
                _context.ExpenseSplits.RemoveRange(splits);

                _context.GroupExpenses.RemoveRange(group.Expenses);
                _context.GroupMembers.RemoveRange(group.Members);
                _context.JoinRequests.RemoveRange(group.JoinRequests);
                _context.Groups.Remove(group);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { message = "Group successfully deleted" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "An error occurred while deleting the group", error = ex.Message });
            }
        }

        // GET: api/Group/Dashboard
        [HttpGet("Dashboard")]
        public async Task<ActionResult<DashboardResponse>> GetDashboard()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Unauthorized("User is not authenticated");
            }

            var createdGroups = await _context.Groups
                .Where(g => g.CreatorId == userId)
                .Select(g => new CreatedGroupDTO
                {
                    Id = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    Code = g.Code,
                    MembersCount = g.Members.Count,
                    PendingRequestsCount = g.JoinRequests.Count(jr => jr.Status == JoinRequestStatus.Pending)
                })
                .ToListAsync();

            var memberGroups = await _context.GroupMembers
                .Where(gm => gm.UserId == userId)
                .Select(gm => new GroupDTO
                {
                    Id = gm.Group.Id,
                    Name = gm.Group.Name,
                    Description = gm.Group.Description,
                    Code = gm.Group.Code,
                    CreatorId = gm.Group.CreatorId
                })
                .ToListAsync();

            var pendingRequests = await _context.JoinRequests
                .Where(jr => jr.UserId == userId && jr.Status == JoinRequestStatus.Pending)
                .Select(jr => new PendingRequestDTO
                {
                    Id = jr.Id,
                    GroupId = jr.GroupId,
                    UserId = jr.UserId,
                    Status = jr.Status,
                    CreatedAt = jr.CreatedAt,
                    Group = new GroupDTO
                    {
                        Id = jr.Group.Id,
                        Name = jr.Group.Name,
                        Description = jr.Group.Description,
                        Code = jr.Group.Code,
                        CreatorId = jr.Group.CreatorId
                    }
                })
                .ToListAsync();

            return Ok(new DashboardResponse
            {
                CreatedGroups = createdGroups,
                MemberGroups = memberGroups,
                PendingRequests = pendingRequests
            });
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

        public class PendingJoinRequestModel
        {
            public string JoinRequestId { get; set; }
            public string GroupId { get; set; }
            public string GroupName { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public class DashboardResponse
        {
            public List<CreatedGroupDTO> CreatedGroups { get; set; }
            public List<GroupDTO> MemberGroups { get; set; }
            public List<PendingRequestDTO> PendingRequests { get; set; }
        }

        public class CreatedGroupDTO
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Code { get; set; }
            public int MembersCount { get; set; }
            public int PendingRequestsCount { get; set; }
        }

        public class GroupDTO
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Code { get; set; }
            public string CreatorId { get; set; }
        }

        public class PendingRequestDTO
        {
            public string Id { get; set; }
            public string GroupId { get; set; }
            public string UserId { get; set; }
            public JoinRequestStatus Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public GroupDTO Group { get; set; }
        }
    }
}
