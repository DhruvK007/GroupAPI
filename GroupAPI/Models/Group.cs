using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace GroupAPI.Models
{
    public class Group
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string Name { get; set; }

        public string Description { get; set; }

        public string Photo { get; set; }

        [Required]
        public string Code { get; set; }

        [Required]
        public string CreatorId { get; set; }

        [ForeignKey("CreatorId")]
        public virtual User Creator { get; set; }

        public virtual ICollection<GroupMember> Members { get; set; }
        public virtual ICollection<GroupExpense> Expenses { get; set; }
    }

    public class GroupMember
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string UserId { get; set; }

        [Required]
        public string GroupId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; }

        [ForeignKey("GroupId")]
        public virtual Group Group { get; set; }
    }

    public class GroupExpense
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string GroupId { get; set; }

        [Required]
        public string PaidById { get; set; }

        public CategoryTypes? Category { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Required]
        public string Description { get; set; }

        [Required]
        public DateTime Date { get; set; }

        [Required]
        public ExpenseStatus Status { get; set; } = ExpenseStatus.Unsettled;

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("GroupId")]
        public virtual Group Group { get; set; }

        [ForeignKey("PaidById")]
        public virtual User PaidBy { get; set; }

        public virtual ICollection<ExpenseSplit> Splits { get; set; }
    }

    public class ExpenseSplit
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string ExpenseId { get; set; }

        [Required]
        public string UserId { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Required]
        public SplitStatus IsPaid { get; set; } = SplitStatus.Unpaid;

        [ForeignKey("ExpenseId")]
        public virtual GroupExpense Expense { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; }

        public virtual ICollection<Payment> Payments { get; set; }
    }

    public class Payment
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string ExpenseSplitId { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Required]
        public DateTime PaidAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("ExpenseSplitId")]
        public virtual ExpenseSplit ExpenseSplit { get; set; }
    }

    public enum CategoryTypes
    {
        Other,
        Bills,
        Food,
        Entertainment,
        Transportation,
        EMI,
        Healthcare,
        Education,
        Investment,
        Shopping,
        Fuel,
        Groceries
    }

    public enum ExpenseStatus
    {
        Unsettled,
        PartiallSettled,
        Settled,
        Cancelled
    }

    public enum SplitStatus
    {
        Unpaid,
        PartiallyPaid,
        Paid
    }
}
