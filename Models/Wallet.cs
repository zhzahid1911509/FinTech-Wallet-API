namespace FinTechWalletAPI.Models
{
    using System.ComponentModel.DataAnnotations;

    public class Wallet
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public User User { get; set; }

        // DECIMAL prevents floating-point precision errors in financial apps
        public decimal Balance { get; set; }

        [Timestamp] // Enforces optimistic concurrency control
        public byte[] RowVersion { get; set; }
    }
}
