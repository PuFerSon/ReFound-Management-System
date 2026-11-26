using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReFound.Data;
using ReFound.Models;

namespace ReFound.Controllers
{
    public class LostItemReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpClientFactory;

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
        private const long MaxFileSizeBytes = 5 * 1024 * 1024;

        public LostItemReportsController(
            ApplicationDbContext context,
            IWebHostEnvironment env,
            IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _env = env;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IActionResult> Index()
        {
            return View(await _context.LostItemReports.ToListAsync());
        }

        public async Task<IActionResult> Details(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var lostItemReport = await _context.LostItemReports
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (lostItemReport == null)
            {
                return NotFound();
            }

            if (lostItemReport.Status != "Found" && lostItemReport.Status != "Resolved")
            {
                ViewBag.PotentialMatches = await FindPotentialMatchesAsync(lostItemReport, cancellationToken);
            }
            else
            {
                ViewBag.PotentialMatches = new List<FoundItem>();
            }

            return View(lostItemReport);
        }

        [Authorize]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Create([Bind("Title,Description,Location,DateLost,ContactInfo")] LostItemReport lostItemReport, IFormFile? imageFile)
        {
            ValidateImageFile(imageFile);

            lostItemReport.Status = "Pending";
            ModelState.Remove(nameof(LostItemReport.Status));

            if (ModelState.IsValid)
            {
                if (imageFile != null && imageFile.Length > 0)
                {
                    lostItemReport.ImagePath = await SaveImageAsync(imageFile);
                }

                _context.Add(lostItemReport);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Details), new { id = lostItemReport.Id });
            }
            return View(lostItemReport);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var lostItemReport = await _context.LostItemReports.FindAsync(id);
            if (lostItemReport == null)
            {
                return NotFound();
            }
            return View(lostItemReport);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Title,Description,Location,DateLost,ContactInfo,Status,ImagePath")] LostItemReport lostItemReport, IFormFile? imageFile)
        {
            if (id != lostItemReport.Id)
            {
                return NotFound();
            }

            ValidateImageFile(imageFile);

            if (ModelState.IsValid)
            {
                try
                {
                    if (imageFile != null && imageFile.Length > 0)
                    {
                        lostItemReport.ImagePath = await SaveImageAsync(imageFile);
                    }

                    _context.Update(lostItemReport);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!LostItemReportExists(lostItemReport.Id))
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
            return View(lostItemReport);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var lostItemReport = await _context.LostItemReports
                .FirstOrDefaultAsync(m => m.Id == id);
            if (lostItemReport == null)
            {
                return NotFound();
            }

            return View(lostItemReport);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var lostItemReport = await _context.LostItemReports.FindAsync(id);
            if (lostItemReport != null)
            {
                _context.LostItemReports.Remove(lostItemReport);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private async Task<List<FoundItem>> FindPotentialMatchesAsync(LostItemReport report, CancellationToken cancellationToken)
        {
            var unclaimed = await _context.FoundItems
                .Where(f => f.Status == "Unclaimed")
                .ToListAsync(cancellationToken);

            if (!unclaimed.Any()) return new List<FoundItem>();

            var queryWords = $"{report.Title} {report.Description} {report.Location}"
                .ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '-', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)
                .ToHashSet();

            var scores = unclaimed.ToDictionary(i => i.Id, _ => 0);

            foreach (var item in unclaimed)
            {
                string combined = $"{item.ItemName} {item.Description} {item.Category} {item.LocationFound}".ToLowerInvariant();
                foreach (var word in queryWords)
                {
                    if (combined.Contains(word)) scores[item.Id] += 10;
                }
            }

            try
            {
                var formattedItems = string.Join("\n", unclaimed.Select(i =>
                    $"ID {i.Id}: {i.ItemName} | Category: {i.Category} | Description: {i.Description} | Location: {i.LocationFound}"
                ));

                var systemPrompt = "Compare LOST ITEM REPORT to UNCLAIMED FOUND ITEMS. Return ONLY plausible matching integer IDs, or empty array [] if none match.";

                var requestPayload = new
                {
                    model = "refound-model",
                    keep_alive = "30m",
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = $"LOST REPORT:\nTitle: {report.Title}\nDescription: {report.Description}\nLast Seen: {report.Location}\n\nUNCLAIMED FOUND ITEMS:\n{formattedItems}" }
                    },
                    stream = false,
                    format = new
                    {
                        type = "object",
                        properties = new
                        {
                            ids = new { type = "array", items = new { type = "integer" } }
                        },
                        required = new[] { "ids" }
                    },
                    options = new { temperature = 0, num_predict = 40 }
                };

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(4);
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:11434/api/chat", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(jsonResponse);
                    var rawResult = doc.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "{}";
                    using var resultDoc = JsonDocument.Parse(rawResult);

                    if (resultDoc.RootElement.TryGetProperty("ids", out var idsElement))
                    {
                        int rankBonus = 25;
                        foreach (var el in idsElement.EnumerateArray())
                        {
                            if (el.TryGetInt32(out int id) && scores.ContainsKey(id))
                            {
                                scores[id] += Math.Max(rankBonus, 10);
                                rankBonus -= 5;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ollama proactive match fallback: {ex.Message}");
            }

            var topIds = scores
                .Where(kv => kv.Value >= 15)
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => kv.Key)
                .ToList();

            return topIds
                .Select(id => unclaimed.First(u => u.Id == id))
                .ToList();
        }

        private bool LostItemReportExists(int id)
        {
            return _context.LostItemReports.Any(e => e.Id == id);
        }

        private void ValidateImageFile(IFormFile? imageFile)
        {
            if (imageFile == null || imageFile.Length == 0) return;

            var ext = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                ModelState.AddModelError("ImagePath", "Only .jpg, .jpeg, .png, and .webp image files are allowed.");
            }

            if (imageFile.Length > MaxFileSizeBytes)
            {
                ModelState.AddModelError("ImagePath", "Image size cannot exceed 5 MB.");
            }
        }

        private async Task<string> SaveImageAsync(IFormFile imageFile)
        {
            string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadsFolder);

            string ext = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
            string uniqueFileName = $"{Guid.NewGuid()}{ext}";
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/uploads/{uniqueFileName}";
        }
    }
}