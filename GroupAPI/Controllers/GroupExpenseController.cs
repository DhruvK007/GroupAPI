using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GroupAPI.Data;
using GroupAPI.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace GroupAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
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
                var errors = ModelState.SelectMany(x => x.Value.Errors)
                                       .Select(x => x.ErrorMessage);
                return BadRequest(new { Message = "Invalid data", Errors = errors });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Verify that both users are members of the group
                var group = await _context.Groups
                    .Include(g => g.Members)
                    .FirstOrDefaultAsync(g => g.Id == request.GroupID);

                if (group == null)
                {
                    return NotFound("Group not found.");
                }

                var payerMember = group.Members.FirstOrDefault(m => m.UserId == request.PayerId);
                var recipientMember = group.Members.FirstOrDefault(m => m.UserId == request.RecipientId);

                if (payerMember == null || recipientMember == null)
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
                    var payment = new Payment
                    {
                        ExpenseSplitId = payerSplit.Id,
                        Amount = expense.Amount,
                        PaidAt = request.TransactionDate
                    };
                    await _context.Payments.AddAsync(payment);

                    // Update split status
                    payerSplit.IsPaid = SplitStatus.Paid;
                    _context.ExpenseSplits.Update(payerSplit);

                    // Update expense status
                    UpdateExpenseStatus(groupExpense);
                    _context.GroupExpenses.Update(groupExpense);
                }

                await _context.SaveChangesAsync();

                // Calculate total amount settled
                decimal totalAmount = request.ExpenseIds.Sum(e => e.Amount);

                // TODO: Implement sendSettleUpNotification method
                // await SendSettleUpNotificationAsync(request.GroupID, request.PayerId, request.RecipientId, totalAmount);

                await transaction.CommitAsync();

                return Ok(new { Message = "Payment to group member completed successfully!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"An error occurred while settling up: {ex.Message}");
            }
        }

        private void UpdateExpenseStatus(GroupExpense expense)
        {
            var allSplitsPaid = expense.Splits.All(s => s.IsPaid == SplitStatus.Paid);
            var someSplitsPaid = expense.Splits.Any(s => s.IsPaid == SplitStatus.Paid);

            if (allSplitsPaid)
            {
                expense.Status = ExpenseStatus.Settled;
            }
            else if (someSplitsPaid)
            {
                expense.Status = ExpenseStatus.PartiallySettled;
            }
            else
            {
                expense.Status = ExpenseStatus.Unsettled;
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
}