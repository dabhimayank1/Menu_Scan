using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class Payment
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid BillId { get; set; }
        public Bill? Bill { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal Amount { get; set; }

        public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

        [MaxLength(100)]
        public string? Reference { get; set; }

        [MaxLength(100)]
        public string? RecordedBy { get; set; }

        public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
    }
}
