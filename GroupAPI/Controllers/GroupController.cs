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
    }
}