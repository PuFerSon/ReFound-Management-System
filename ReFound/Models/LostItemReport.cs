using System.ComponentModel.DataAnnotations;

namespace ReFound.Models
{
    public class LostItemReport
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty;

        [Required]
        public string Location { get; set; } = string.Empty;

        public DateTime DateLost { get; set; } = DateTime.Now;

        [Required]
        public string ContactInfo { get; set; } = string.Empty;

        public string Status { get; set; } = "Pending";
    }
}