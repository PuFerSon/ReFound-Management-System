using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ReFound.Data;
using ReFound.Models;

namespace ReFound.Controllers
{
    [Authorize]
    public class ClaimRequestsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ClaimRequestsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var query = _context.ClaimRequests.AsQueryable();

            if (!User.IsInRole("Admin"))
            {
                var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (int.TryParse(userIdStr, out int userId))
                {
                    query = query.Where(c => c.ClaimantUserId == userId);
                }
            }

            var claims = await query.OrderByDescending(c => c.ClaimDate).ToListAsync();

            ViewBag.ItemNames = await _context.FoundItems
                .ToDictionaryAsync(f => f.Id, f => f.ItemName);

            ViewBag.UserNames = await _context.Users
                .ToDictionaryAsync(u => u.Id, u => u.FullName);

            return View(claims);
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var claimRequest = await _context.ClaimRequests
                .FirstOrDefaultAsync(m => m.Id == id);
            if (claimRequest == null)
            {
                return NotFound();
            }

            if (!User.IsInRole("Admin"))
            {
                var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!int.TryParse(userIdStr, out int userId) || claimRequest.ClaimantUserId != userId)
                {
                    return Forbid();
                }
            }

            if (claimRequest.FoundItemId.HasValue)
            {
                ViewBag.FoundItem = await _context.FoundItems.FindAsync(claimRequest.FoundItemId.Value);
            }

            return View(claimRequest);
        }

        public async Task<IActionResult> Create(int? foundItemId)
        {
            await PopulateFoundItemsDropDown(foundItemId);

            var model = new ClaimRequest
            {
                FoundItemId = foundItemId,
                ClaimDate = DateTime.Now
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("FoundItemId,ProofOfOwnership")] ClaimRequest claimRequest)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? parsedUserId = null;
            if (int.TryParse(userIdStr, out int userId))
            {
                claimRequest.ClaimantUserId = userId;
                parsedUserId = userId;
            }

            if (claimRequest.FoundItemId.HasValue)
            {
                var targetItem = await _context.FoundItems.FindAsync(claimRequest.FoundItemId.Value);
                if (targetItem == null || targetItem.Status == "Claimed")
                {
                    ModelState.AddModelError("FoundItemId", "This item is no longer available to claim.");
                }
                else if (parsedUserId.HasValue)
                {
                    bool alreadyPending = await _context.ClaimRequests.AnyAsync(c =>
                        c.FoundItemId == claimRequest.FoundItemId.Value &&
                        c.ClaimantUserId == parsedUserId.Value &&
                        c.Status == "Pending");

                    if (alreadyPending)
                    {
                        ModelState.AddModelError("FoundItemId", "You already have a pending claim request for this item.");
                    }
                }
            }

            claimRequest.ClaimDate = DateTime.Now;
            claimRequest.Status = "Pending";

            ModelState.Remove(nameof(ClaimRequest.ClaimDate));

            if (ModelState.IsValid)
            {
                _context.Add(claimRequest);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }

            await PopulateFoundItemsDropDown(claimRequest.FoundItemId);
            return View(claimRequest);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Approve(int id)
        {
            var claimRequest = await _context.ClaimRequests.FindAsync(id);
            if (claimRequest == null)
            {
                return NotFound();
            }

            var adminName = User.Identity?.Name ?? "Admin";
            claimRequest.Status = "Approved";
            claimRequest.ResolvedAt = DateTime.Now;
            claimRequest.ResolvedBy = adminName;

            await SyncClaimResolutionAsync(claimRequest, adminName);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Details), new { id = claimRequest.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Reject(int id)
        {
            var claimRequest = await _context.ClaimRequests.FindAsync(id);
            if (claimRequest == null)
            {
                return NotFound();
            }

            claimRequest.Status = "Rejected";
            claimRequest.ResolvedAt = DateTime.Now;
            claimRequest.ResolvedBy = User.Identity?.Name ?? "Admin";

            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Details), new { id = claimRequest.Id });
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var claimRequest = await _context.ClaimRequests.FindAsync(id);
            if (claimRequest == null)
            {
                return NotFound();
            }

            if (claimRequest.FoundItemId.HasValue)
            {
                ViewBag.FoundItem = await _context.FoundItems.FindAsync(claimRequest.FoundItemId.Value);
            }

            if (claimRequest.ClaimantUserId.HasValue)
            {
                ViewBag.Claimant = await _context.Users.FindAsync(claimRequest.ClaimantUserId.Value);
            }

            return View(claimRequest);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id, [Bind("Id,FoundItemId,ClaimantUserId,ClaimDate,ProofOfOwnership,Status,ResolvedAt,ResolvedBy")] ClaimRequest claimRequest)
        {
            if (id != claimRequest.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var adminName = User.Identity?.Name ?? "Admin";

                    if ((claimRequest.Status == "Approved" || claimRequest.Status == "Rejected") && !claimRequest.ResolvedAt.HasValue)
                    {
                        claimRequest.ResolvedAt = DateTime.Now;
                        claimRequest.ResolvedBy = adminName;
                    }
                    else if (claimRequest.Status == "Pending")
                    {
                        claimRequest.ResolvedAt = null;
                        claimRequest.ResolvedBy = null;
                    }

                    _context.Update(claimRequest);

                    if (claimRequest.Status == "Approved")
                    {
                        await SyncClaimResolutionAsync(claimRequest, adminName);
                    }
                    else if (claimRequest.FoundItemId.HasValue)
                    {
                        bool hasOtherApprovedClaim = await _context.ClaimRequests.AnyAsync(c =>
                            c.FoundItemId == claimRequest.FoundItemId.Value &&
                            c.Id != claimRequest.Id &&
                            c.Status == "Approved");

                        if (!hasOtherApprovedClaim)
                        {
                            var foundItem = await _context.FoundItems.FindAsync(claimRequest.FoundItemId.Value);
                            if (foundItem != null)
                            {
                                foundItem.Status = "Unclaimed";
                            }
                        }
                    }

                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ClaimRequestExists(claimRequest.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }

            if (claimRequest.FoundItemId.HasValue)
            {
                ViewBag.FoundItem = await _context.FoundItems.FindAsync(claimRequest.FoundItemId.Value);
            }

            if (claimRequest.ClaimantUserId.HasValue)
            {
                ViewBag.Claimant = await _context.Users.FindAsync(claimRequest.ClaimantUserId.Value);
            }

            return View(claimRequest);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var claimRequest = await _context.ClaimRequests
                .FirstOrDefaultAsync(m => m.Id == id);
            if (claimRequest == null)
            {
                return NotFound();
            }

            return View(claimRequest);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var claimRequest = await _context.ClaimRequests.FindAsync(id);
            if (claimRequest != null)
            {
                _context.ClaimRequests.Remove(claimRequest);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private async Task SyncClaimResolutionAsync(ClaimRequest approvedClaim, string adminName)
        {
            if (!approvedClaim.FoundItemId.HasValue) return;

            var foundItem = await _context.FoundItems.FindAsync(approvedClaim.FoundItemId.Value);
            if (foundItem != null)
            {
                foundItem.Status = "Claimed";
            }

            var competingClaims = await _context.ClaimRequests
                .Where(c => c.FoundItemId == approvedClaim.FoundItemId
                         && c.Id != approvedClaim.Id
                         && c.Status == "Pending")
                .ToListAsync();

            foreach (var other in competingClaims)
            {
                other.Status = "Rejected";
                other.ResolvedAt = DateTime.Now;
                other.ResolvedBy = $"{adminName} (Auto-closed: item claimed)";
            }
        }

        private bool ClaimRequestExists(int id)
        {
            return _context.ClaimRequests.Any(e => e.Id == id);
        }

        private async Task PopulateFoundItemsDropDown(int? selectedId = null)
        {
            var unclaimedItems = await _context.FoundItems
                .Where(f => f.Status == "Unclaimed" || f.Id == selectedId)
                .Select(f => new
                {
                    f.Id,
                    DisplayText = $"{f.ItemName} ({f.LocationFound})"
                })
                .ToListAsync();

            ViewBag.FoundItems = new SelectList(unclaimedItems, "Id", "DisplayText", selectedId);
        }
    }
}