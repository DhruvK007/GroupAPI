using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using GroupAPI.Data;
using GroupAPI.Models;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;
using static GroupAPI.Controllers.GroupController.JoinRequestResponseDTO;

namespace GroupAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GroupController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<GroupController> _logger;
        private object users;

        public GroupController(ApplicationDbContext context, ILogger<GroupController> logger)
        {
            _context = context;
            _logger = logger;
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
        public async Task<ActionResult<JoinRequestResponseDTO>> CreateJoinRequest(JoinRequestModel model)
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

            // Create response DTO matching frontend expectations
            var response = new JoinRequestResponseDTO
            {
                Id = joinRequest.Id,
                GroupId = joinRequest.GroupId,
                UserId = joinRequest.UserId,
                Status = joinRequest.Status,
                CreatedAt = joinRequest.CreatedAt,
                Group = new GroupDTO
                {
                    Id = group.Id,
                    Name = group.Name,
                    Description = group.Description,
                    Code = group.Code,
                    CreatorId = group.CreatorId
                }
            };

            return Ok(response);
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

        [HttpPost("CancelJoinRequest")]
        public async Task<IActionResult> CancelJoinRequest([FromBody] CancelJoinRequestModel model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Unauthorized("User is not authenticated");
            }

            var joinRequest = await _context.JoinRequests
                .FirstOrDefaultAsync(jr => jr.Id == model.JoinRequestId && jr.UserId == userId);

            if (joinRequest == null)
            {
                return NotFound("Join request not found or you are not authorized to cancel it");
            }

            if (joinRequest.Status != JoinRequestStatus.Pending)
            {
                return BadRequest("Only pending join requests can be cancelled");
            }

            _context.JoinRequests.Remove(joinRequest);
            await _context.SaveChangesAsync();

            return Ok("Join request cancelled successfully");
        }

        [HttpGet("CreatorCheck")]
        public async Task<IActionResult> CreatorCheck(string id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (userId == null)
            {
                return Unauthorized("User is not authenticated");
            }

            var group = await _context.Groups.FindAsync(id);

            if (group == null)
            {
                return NotFound("Group not found");
            }

            if (group.CreatorId != userId)
            {
                return Forbid("User is not authorized to access this group");
            }

            return Ok();
        }

        [HttpGet("{id}/JoinRequests")]
        public async Task<ActionResult<List<JoinRequestResponse2DTO>>> GetJoinRequests(string id)
        {
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userId == null)
                {
                    return Unauthorized("User is not authenticated");
                }

                var group = await _context.Groups
                    .Include(g => g.JoinRequests)
                        .ThenInclude(jr => jr.User)
                    .FirstOrDefaultAsync(g => g.Id == id);

                if (group == null)
                {
                    return NotFound("Group not found");
                }

                if (group.CreatorId != userId)
                {
                    return Forbid("User is not authorized to access this group's join requests");
                }

                var joinRequests = group.JoinRequests
                    .Where(jr => jr.Status == JoinRequestStatus.Pending)
                    .Select(jr => new JoinRequestResponse2DTO
                    {
                        Id = jr.Id,
                        GroupId = jr.GroupId,
                        UserId = jr.UserId,
                        Status = jr.Status,
                        CreatedAt = jr.CreatedAt,
                        User = new UserDTO
                        {
                            Id = jr.User.Id,
                            Name = jr.User.Name,
                            Email = jr.User.Email
                        },
                        Group = new GroupDTO
                        {
                            Id = group.Id,
                            Name = group.Name,
                            Description = group.Description,
                            Code = group.Code,
                            CreatorId = group.CreatorId
                        }
                    })
                    .ToList();

                return Ok(joinRequests);
            }
        }
        [HttpGet("{groupId}/PageData")]
        public async Task<ActionResult<GroupPageData>> GetGroupPageData(string groupId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Unauthorized();
            }

            var group = await _context.Groups
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == groupId && g.Members.Any(m => m.UserId == userId));

            if (group == null &&  groupId == null && userId == null)
            {
                return NotFound("Group not found or user is not a member");
            }

            var transactionData = await GetGroupTransactionData(groupId);
            var balanceData = await FetchGroupBalances(groupId);
            var usersToPayData = await GetUsersToPay(userId, groupId);

            Console.WriteLine("Users to pay: ");
            Console.WriteLine(balanceData.ToArray());

            var currentUserBalance = balanceData.FirstOrDefault(b => b.UserId == userId);
            var leaveStatus = new LeaveStatus
            {
                Status = currentUserBalance?.Amount > 0 ? "gets back" : "owes",
                Amount = Math.Abs(currentUserBalance?.Amount ?? 0),
                UserId = userId,
                GroupId = groupId
            };

            var groupMembers = await _context.GroupMembers
                .Where(gm => gm.GroupId == groupId)
                .Select(gm => new GroupMemberData
                {
                    UserId = gm.UserId,
                    Name = gm.User.Name
                })
                .ToListAsync();

            return new GroupPageData
            {
                GroupName = group.Name,
                CreatorId = group.CreatorId,
                UserName = User.FindFirst(ClaimTypes.Name)?.Value?.Split(' ')[0],
                UserId = userId,
                Leave = leaveStatus,
                GroupMembers = groupMembers,
                TransactionData = transactionData.Select(td => new DetailedTransactionData
                {
                    Id = td.Id,
                    Description = td.Description,
                    Amount = td.Amount,
                    Date = td.Date,
                    PaidBy = td.PaidBy,
                    Category = td.Category,
                    Splits = td.Splits,
                    Status = td.Status
                }).ToList(),
                UsersYouNeedToPay = usersToPayData.Select(utp => new UserToPayData
                {
                    UserId = utp.UserId,
                    Name = utp.Name,
                    Amount = utp.Amount,
                    Transactions = utp.Transactions.Select(td => new TransactionDebtData
                    {
                        ExpenseId = td.ExpenseId,
                        Description = td.Description,
                        Amount = td.Amount,
                        Date = td.Date,
                        Status = td.Status
                    }).ToList(),
                }).ToList(),
                Balance = balanceData
            };

        }


        private async Task<List<UserToPayData>> GetUsersToPay(string userId, string groupId)
        {
            var expenseSplits = await _context.ExpenseSplits
                .Where(es => es.UserId == userId &&
                             es.Expense.GroupId == groupId &&
                             ((int)es.IsPaid == 0 || (int)es.IsPaid == 1)) // 0 for Unpaid, 1 for PartiallyPaid
                .Select(es => new
                {
                    es.Id,
                    es.Amount,
                    es.ExpenseId,
                    PaidById = es.Expense.PaidBy.Id,
                    PaidByName = es.Expense.PaidBy.Name,
                    Payments = es.Payments.Select(p => new { p.Amount }).ToList(),
                    IsPaid = (int)es.IsPaid
                })
                .ToListAsync();

            return expenseSplits
                .Select(es => new
                {
                    Id = es.Id,
                    GroupExpenseId = es.Id,
                    MemberName = es.PaidByName,
                    MemberId = es.PaidById,
                    AmountToPay = es.Amount - es.Payments.Sum(p => p.Amount),
                    Status = (SplitStatus)es.IsPaid
                })
                .Where(payment => payment.AmountToPay > 0 && payment.MemberId != userId)
                .GroupBy(payment => payment.MemberId)
                .Select(group => new UserToPayData
                {
                    UserId = group.Key,
                    Name = group.First().MemberName,
                    Amount = group.Sum(p => p.AmountToPay),
                    Transactions = group.Select(p => new TransactionDebtData
                    {
                        ExpenseId = p.GroupExpenseId,
                        Description = $"Expense {p.GroupExpenseId}",
                        Amount = p.AmountToPay,
                        Date = DateTime.Now, // You might want to fetch the actual date from the database
                        Status = p.Status
                    }).ToList()
                })
                .ToList();

           
        }

        public class UserToPayData
        {
            public string UserId { get; set; }
            public string Name { get; set; }
            public decimal Amount { get; set; }
            public List<TransactionDebtData> Transactions { get; set; }
        }

        public class TransactionDebtData
        {
            public string ExpenseId { get; set; }
            public string Description { get; set; }
            public decimal Amount { get; set; }
            public DateTime Date { get; set; }
            public SplitStatus Status { get; set; }
        }



        public class UserPaymentInfo
        {
            public string ExpenseId { get; set; }
            public decimal Amount { get; set; }
            public SplitStatus Status { get; set; }
            public string MemberId { get; set; }
            public string PaidTo { get; set; }
            public string GroupName { get; set; }
        }


        private Task<List<DetailedTransactionData>> GetGroupTransactionData(string groupId)
        {
            return GetGroupTransactionData(groupId, SplitStatus.Unpaid);
        }

        private async Task<List<DetailedTransactionData>> GetGroupTransactionData(string groupId, SplitStatus Status)
        {
            return await _context.GroupExpenses
                .Where(ge => ge.GroupId == groupId)
                .Select(ge => new DetailedTransactionData
                {
                    Id = ge.Id,
                    Description = ge.Description,
                    Amount = ge.Amount,
                    Date = ge.Date,
                    PaidBy = ge.PaidBy.Name,
                    Category = ge.Category.ToString(),
                    Status = ge.Status,
                    Splits = ge.Splits.Select(s => new SplitData
                    {
                        UserId = s.UserId,
                        UserName = s.User.Name,
                        Amount = s.Amount,
                        Status = s.IsPaid
                    }).ToList()
                })
                .ToListAsync();
        }

        private async Task<List<BalanceData>> FetchGroupBalances(string groupId)
        {
            var expenses = await _context.GroupExpenses
                .Where(ge => ge.GroupId == groupId)
                .Include(ge => ge.Splits)
                .ToListAsync();

            var balances = new Dictionary<string, decimal>();

            foreach (var expense in expenses)
            {
                if (!balances.ContainsKey(expense.PaidById))
                {
                    balances[expense.PaidById] = 0;
                }
                balances[expense.PaidById] += expense.Amount;

                foreach (var split in expense.Splits)
                {
                    if (!balances.ContainsKey(split.UserId))
                    {
                        balances[split.UserId] = 0;
                    }
                    balances[split.UserId] -= split.Amount;
                }
            }

            var users = await _context.Users
                .Where(u => balances.Keys.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u);

            return balances.Select(kvp => new BalanceData
            {
                UserId = kvp.Key,
                Name = users[kvp.Key].Name,
                Amount = kvp.Value,
                Status = kvp.Value > 0 ? "gets back" : "owes"
            }).ToList();
        }


        // Get detailed list of users you need to pay
       


        // Transaction data
        public class DetailedTransactionData
        {
            public string Id { get; set; }
            public string Description { get; set; }
            public decimal Amount { get; set; }
            public DateTime Date { get; set; }
            public string PaidBy { get; set; }
            public string Category { get; set; }
            public ExpenseStatus Status { get; set; }
            public List<SplitData> Splits { get; set; }
        }

        // Split data
        public class SplitData
        {
            public string UserId { get; set; }
            public string UserName { get; set; }
            public decimal Amount { get; set; }
            public SplitStatus Status { get; set; }
        }

      


        //// Transaction debt data
        //public class TransactionDebtData
        //{
        //    public string ExpenseId { get; set; } // Add this field
        //    public string Description { get; set; }
        //    public decimal Amount { get; set; }
        //    public DateTime Date { get; set; }
        //    public SplitStatus Status { get; set; } // The status of each transaction
        //}


        // Group page data
        public class GroupPageData
        {
            public string GroupName { get; set; }
            public string CreatorId { get; set; }
            public string UserName { get; set; }
            public string UserId { get; set; }
            public LeaveStatus Leave { get; set; }
            public List<GroupMemberData> GroupMembers { get; set; }
            public List<UserToPayData> UsersYouNeedToPay { get; set; }
            public List<DetailedTransactionData> TransactionData { get; set; }
            public List<BalanceData> Balance { get; set; }
        }

        // Leave status

        public class LeaveStatus
    {
        public string Status { get; set; }
        public decimal Amount { get; set; }
        public string UserId { get; set; }
        public string GroupId { get; set; }
    }

    // Group member data
    public class GroupMemberData
    {
        public string UserId { get; set; }
        public string Name { get; set; }
    }

        // User to pay data

    //public class UserToPayData
    //{
    //        internal List<TransactionDebtData> Transactions;
    //        internal SplitStatus Status;

    //        public string UserId { get; set; }
    //    public string Name { get; set; }
    //    public decimal Amount { get; set; }
    //        public object Value { get; internal set; }
    //    }


        // Transaction data
    public class TransactionData
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public decimal Amount { get; set; }
        public DateTime Date { get; set; }
        public string PaidBy { get; set; }
        public string Category { get; set; }
    }

        // Balance data
    public class BalanceData
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; }
    }


       //

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

            public string Description { get; set; } = "";
            public string Photo { get; set; } = "";
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

        public class CancelJoinRequestModel
        {
            [Required]
            public string JoinRequestId { get; set; }
        }

        public class JoinRequestResponseDTO
        {
            public string Id { get; set; }
            public string GroupId { get; set; }
            public string UserId { get; set; }
            public JoinRequestStatus Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public GroupDTO Group { get; set; }


            public class JoinRequestResponse2DTO
            {
                public string Id { get; set; }
                public string GroupId { get; set; }
                public string UserId { get; set; }
                public JoinRequestStatus Status { get; set; }
                public DateTime CreatedAt { get; set; }
                public UserDTO User { get; set; }
                public GroupDTO Group { get; set; }
            }
            public class UserDTO
            {
                public string Id { get; set; }
                public string Name { get; set; }
                public string Email { get; set; }
            }
        }
    }
}
