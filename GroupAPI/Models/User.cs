using System.ComponentModel.DataAnnotations;

namespace GroupAPI.Models
{
    public class User
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string Name { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [Required]
        public string Password { get; set; }

        public virtual ICollection<Group> CreatedGroups { get; set; }
        public virtual ICollection<GroupMember> GroupMemberships { get; set; }
        public virtual ICollection<GroupExpense> PaidExpenses { get; set; }
        public virtual ICollection<ExpenseSplit> ExpenseSplits { get; set; }
    }
}
