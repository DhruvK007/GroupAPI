using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GroupAPI.Data;
using GroupAPI.Models;

namespace GroupAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GroupExpenseController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public GroupExpenseController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost("AddExpense")]
        public async Task<IActionResult> AddExpense([FromBody] AddExpenseRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Check if the group exists
                var group = await _context.Groups
                    .Include(g => g.Members)
                    .FirstOrDefaultAsync(g => g.Id == request.GroupId);

                if (group == null)
                {
                    return NotFound("Group not found");
                }

                // Check if the payer is a member of the group
                if (!group.Members.Any(m => m.UserId == request.PaidById))
                {
                    return BadRequest("Payer is not a member of the group");
                }

                // Create the new expense
                var newExpense = new GroupExpense
                {
                    GroupId = request.GroupId,
                    PaidById = request.PaidById,
                    Category = request.Category,
                    Amount = request.Amount,
                    Description = request.Title,
                    Date = request.Date,
                    Status = ExpenseStatus.Unsettled
                };

                _context.GroupExpenses.Add(newExpense);
                await _context.SaveChangesAsync();

                // Create expense splits
                foreach (var split in request.Splits)
                {
                    var expenseSplit = new ExpenseSplit
                    {
                        ExpenseId = newExpense.Id,
                        UserId = split.UserId,
                        Amount = split.Amount,
                        IsPaid = split.UserId == request.PaidById ? SplitStatus.Paid : SplitStatus.Unpaid
                    };

                    _context.ExpenseSplits.Add(expenseSplit);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Success = true, ExpenseId = newExpense.Id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"An error occurred while adding the expense: {ex.Message}");
            }
        }

        [HttpPost("SettleUp")]
        public async Task<IActionResult> SettleUp([FromBody] SettleUpRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Verify that both users are members of the group
                var groupMembers = await _context.GroupMembers
                    .Where(gm => gm.GroupId == request.GroupID)
                    .Select(gm => gm.UserId)
                    .ToListAsync();

                if (!groupMembers.Contains(request.PayerId) || !groupMembers.Contains(request.RecipientId))
                {
                    return BadRequest("Both users must be members of the group.");
                }

                // Fetch all relevant group expenses
                var groupExpenses = await _context.GroupExpenses
                    .Where(ge => request.ExpenseIds.Select(e => e.GroupExpenseId).Contains(ge.Id))
                    .Where(ge => ge.GroupId == request.GroupID)
                    .Where(ge => ge.PaidById == request.RecipientId)
                    .Where(ge => ge.Status != ExpenseStatus.Cancelled)
                    .Include(ge => ge.Splits)
                    .ToListAsync();

                var updates = new List<Task>();

                foreach (var expense in request.ExpenseIds)
                {
                    var groupExpense = groupExpenses.FirstOrDefault(ge => ge.Id == expense.GroupExpenseId);
                    if (groupExpense == null)
                    {
                        continue;
                    }

                    var payerSplit = groupExpense.Splits.FirstOrDefault(s => s.UserId == request.PayerId);
                    if (payerSplit == null)
                    {
                        continue;
                    }

                    // Create payment
                    var task = _context.Payments.AddAsync(new Payment
                    {
                        ExpenseSplitId = expense.ExpenseId,
                        Amount = expense.Amount,
                        PaidAt = request.TransactionDate
                    }).AsTask();

                    updates.Add(task);

                    // Update split status
                    payerSplit.IsPaid = SplitStatus.Paid;
                    _context.ExpenseSplits.Update(payerSplit);

                    // Update expense status
                    var allSplitsPaid = groupExpense.Splits.All(s => s.IsPaid == SplitStatus.Paid);
                    var someSplitsPaid = groupExpense.Splits.Any(s => s.IsPaid == SplitStatus.Paid);

                    if (allSplitsPaid)
                    {
                        groupExpense.Status = ExpenseStatus.Settled;
                    }
                    else if (someSplitsPaid)
                    {
                        groupExpense.Status = ExpenseStatus.PartiallySettled;
                    }
                    else
                    {
                        groupExpense.Status = ExpenseStatus.Unsettled;
                    }

                    _context.GroupExpenses.Update(groupExpense);
                }

                await Task.WhenAll(updates);
                await _context.SaveChangesAsync();

                // Calculate total amount settled
                decimal totalAmount = request.ExpenseIds.Sum(e => e.Amount);

                // TODO: Implement sendSettleUpNotification method
                // sendSettleUpNotification(request.GroupID, request.PayerId, request.RecipientId, totalAmount);

                await transaction.CommitAsync();

                // TODO: Implement revalidatePath method or remove if not applicable in your backend
                // revalidatePath($"/group/{request.GroupID}");

                return Ok(new { Message = "Payment to group member completed successfully!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"An error occurred while settling up: {ex.Message}");
            }
        }
    }

    public class AddExpenseRequest
    {
        public string GroupId { get; set; }
        public string PaidById { get; set; }
        public CategoryTypes Category { get; set; }
        public decimal Amount { get; set; }
        public string Title { get; set; }
        public DateTime Date { get; set; }
        public List<ExpenseSplitRequest> Splits { get; set; }
    }

    public class ExpenseSplitRequest
    {
        public string UserId { get; set; }
        public decimal Amount { get; set; }
    }

    public class SettleUpRequest
    {
        public string GroupID { get; set; }
        public string PayerId { get; set; }
        public string RecipientId { get; set; }
        public List<ExpenseDetail> ExpenseIds { get; set; }
        public DateTime TransactionDate { get; set; }
    }

    public class ExpenseDetail
    {
        public string ExpenseId { get; set; }
        public decimal Amount { get; set; }
        public string GroupExpenseId { get; set; }
    }
}