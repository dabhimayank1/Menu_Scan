using System.ComponentModel.DataAnnotations;
using MenuScan.Models.Entities;

namespace MenuScan.Models.ViewModels
{
    public class FeedbackSubmissionDto
    {
        [Required]
        public string OrderToken { get; set; } = string.Empty;

        [Range(1, 5)]
        public int OverallRating { get; set; } = 5;

        [Range(1, 5)]
        public int FoodRating { get; set; } = 5;

        [Range(1, 5)]
        public int ServiceRating { get; set; } = 5;

        public string? CustomerName { get; set; }

        public List<string>? Tags { get; set; }

        public string? Comment { get; set; }
    }

    public class AdminFeedbackViewModel
    {
        public Restaurant Restaurant { get; set; } = null!;
        public List<CustomerFeedbackItemDto> Feedbacks { get; set; } = new();

        public int TotalReviews => Feedbacks.Count;
        public double AverageOverallRating => TotalReviews == 0 ? 0 : Math.Round(Feedbacks.Average(f => f.OverallRating), 1);
        public double AverageFoodRating => TotalReviews == 0 ? 0 : Math.Round(Feedbacks.Average(f => f.FoodRating), 1);
        public double AverageServiceRating => TotalReviews == 0 ? 0 : Math.Round(Feedbacks.Average(f => f.ServiceRating), 1);

        public int FiveStarCount => Feedbacks.Count(f => f.OverallRating == 5);
        public int FourStarCount => Feedbacks.Count(f => f.OverallRating == 4);
        public int ThreeStarCount => Feedbacks.Count(f => f.OverallRating == 3);
        public int TwoStarCount => Feedbacks.Count(f => f.OverallRating == 2);
        public int OneStarCount => Feedbacks.Count(f => f.OverallRating == 1);

        public int FiveStarPercent => TotalReviews == 0 ? 0 : (int)Math.Round((double)FiveStarCount / TotalReviews * 100);
        public int PositiveCount => Feedbacks.Count(f => f.OverallRating >= 4);
        public int CriticalCount => Feedbacks.Count(f => f.OverallRating <= 3);

        public List<KeyValuePair<string, int>> TopTags { get; set; } = new();
    }

    public class CustomerFeedbackItemDto
    {
        public Guid Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string TableNumber { get; set; } = string.Empty;
        public int OverallRating { get; set; }
        public int FoodRating { get; set; }
        public int ServiceRating { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string? Comment { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string TimeAgo { get; set; } = string.Empty;
    }
}
