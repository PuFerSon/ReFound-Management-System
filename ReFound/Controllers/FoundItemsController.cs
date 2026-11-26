using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReFound.Data;
using ReFound.Models;

namespace ReFound.Controllers
{
    public class FoundItemsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _env;

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
        private const long MaxFileSizeBytes = 5 * 1024 * 1024;

        public FoundItemsController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IWebHostEnvironment env)
        {
            _context = context;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _env = env;
        }

        public async Task<IActionResult> Index()
        {
            return View(await _context.FoundItems.ToListAsync());
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ExportCsv()
        {
            var items = await _context.FoundItems
                .OrderByDescending(i => i.Id)
                .ToListAsync();

            var csv = new StringBuilder();
            csv.AppendLine("ID,Item Name,Category,Location Found,Date Found,Status,Admin Verification Notes");

            foreach (var item in items)
            {
                csv.AppendLine(string.Join(",",
                    item.Id,
                    EscapeCsv(item.ItemName),
                    EscapeCsv(item.Category),
                    EscapeCsv(item.LocationFound),
                    EscapeCsv(item.DateFound.ToString("yyyy-MM-dd")),
                    EscapeCsv(item.Status),
                    EscapeCsv(item.AdminNotes)
                ));
            }

            var fileName = $"ReFound_Inventory_{DateTime.Now:yyyy-MM-dd}.csv";
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return File(bytes, "text/csv", fileName);
        }

        [HttpGet("/FoundItems/SmartSearch")]
        public async Task<IActionResult> SmartSearch(string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Json(await _context.FoundItems.Select(i => i.Id).ToListAsync(cancellationToken));
            }

            var items = await _context.FoundItems
                .Select(i => new { i.Id, i.ItemName, i.Category, i.Description, i.LocationFound })
                .ToListAsync(cancellationToken);

            var stopWords = new HashSet<string>
            {
                "the", "a", "an", "and", "or", "for", "with", "in", "on", "at", "to", "of",
                "my", "i", "it", "is", "was", "left", "lost", "found", "something", "thing",
                "things", "item", "items", "stuff", "used", "where", "people", "near", "by", "from"
            };

            var queryWords = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '!', '?', '-', '_', '/', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 1 && !stopWords.Contains(w))
                .ToHashSet();

            var keywordScores = items.ToDictionary(i => i.Id, _ => 0);

            foreach (var item in items)
            {
                string title = item.ItemName.ToLowerInvariant();
                string desc = item.Description.ToLowerInvariant();
                string cat = item.Category.ToLowerInvariant();
                string loc = item.LocationFound.ToLowerInvariant();

                foreach (var word in queryWords)
                {
                    if (title.Contains(word)) keywordScores[item.Id] += 30;
                    if (desc.Contains(word)) keywordScores[item.Id] += 20;
                    if (cat.Contains(word)) keywordScores[item.Id] += 10;
                    if (loc.Contains(word)) keywordScores[item.Id] += 20;
                }
            }

            try
            {
                var formattedItems = string.Join("\n", items.Select(i =>
                    $"{i.Id}: {i.ItemName} [{i.Category}] - {i.Description} | Found: {i.LocationFound}"
                ));

                var requestPayload = new
                {
                    model = "refound-model",
                    keep_alive = "30m",
                    messages = new[]
                    {
                        new { role = "user", content = $"Query: {query}\nItems:\n{formattedItems}" }
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
                    options = new { temperature = 0, num_predict = 150 }
                };

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:11434/api/chat", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(jsonResponse);
                    var rawResult = doc.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "{}";

                    int startIdx = rawResult.IndexOf('{');
                    int endIdx = rawResult.IndexOf('}');
                    if (startIdx >= 0 && endIdx > startIdx)
                    {
                        rawResult = rawResult.Substring(startIdx, endIdx - startIdx + 1);
                    }

                    using var resultDoc = JsonDocument.Parse(rawResult);

                    if (resultDoc.RootElement.TryGetProperty("ids", out var idsElement))
                    {
                        var aiIds = new List<int>();
                        foreach (var el in idsElement.EnumerateArray())
                        {
                            if (el.TryGetInt32(out int id) && keywordScores.ContainsKey(id) && !aiIds.Contains(id))
                            {
                                aiIds.Add(id);
                            }
                        }

                        var exactKeywordIds = keywordScores
                            .Where(kv => kv.Value > 0 && !aiIds.Contains(kv.Key))
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key);

                        return Json(aiIds.Concat(exactKeywordIds).ToList());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Json(new List<int>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ollama fallback to keyword search: {ex.Message}");
            }

            var fallbackIds = keywordScores
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            return Json(fallbackIds);
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var foundItem = await _context.FoundItems
                .FirstOrDefaultAsync(m => m.Id == id);
            if (foundItem == null)
            {
                return NotFound();
            }

            return View(foundItem);
        }

        [Authorize(Roles = "Admin")]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create([Bind("Id,ItemName,Description,Category,DateFound,LocationFound,Status,AdminNotes")] FoundItem foundItem, IFormFile? imageFile)
        {
            ValidateImageFile(imageFile);

            if (ModelState.IsValid)
            {
                if (imageFile != null && imageFile.Length > 0)
                {
                    foundItem.ImagePath = await SaveImageAsync(imageFile);
                }

                _context.Add(foundItem);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(foundItem);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var foundItem = await _context.FoundItems.FindAsync(id);
            if (foundItem == null)
            {
                return NotFound();
            }
            return View(foundItem);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id, [Bind("Id,ItemName,Description,Category,DateFound,LocationFound,Status,ImagePath,AdminNotes")] FoundItem foundItem, IFormFile? imageFile)
        {
            if (id != foundItem.Id)
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
                        foundItem.ImagePath = await SaveImageAsync(imageFile);
                    }

                    _context.Update(foundItem);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!FoundItemExists(foundItem.Id))
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
            return View(foundItem);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var foundItem = await _context.FoundItems
                .FirstOrDefaultAsync(m => m.Id == id);
            if (foundItem == null)
            {
                return NotFound();
            }

            return View(foundItem);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var foundItem = await _context.FoundItems.FindAsync(id);
            if (foundItem != null)
            {
                _context.FoundItems.Remove(foundItem);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool FoundItemExists(int id)
        {
            return _context.FoundItems.Any(e => e.Id == id);
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return $"\"{value.Replace("\"", "\"\"").Replace("\r\n", " ").Replace("\n", " ")}\"";
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