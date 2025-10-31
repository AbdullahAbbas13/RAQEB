namespace Raqeb.Shared.Models
{

namespace Raqeb.Shared.Models
    {
        public class PDObservedRate
        {
            [Key]
            public int Id { get; set; }
            public int PoolId { get; set; }
            [MaxLength(200)] public string PoolName { get; set; }
            public int Version { get; set; }
            public double ObservedDefaultRate { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }
    }

}
