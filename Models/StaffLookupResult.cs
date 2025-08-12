namespace CallAutomation.AzureAI.VoiceLive.Models
{
    public enum StaffLookupStatus
    {
        Authorized,
        NotAuthorized,
        MultipleFound,
        NotFound
    }

    public class StaffLookupResult
    {
        public StaffLookupStatus Status { get; set; }
        public string? Email { get; set; }
        public string? RowKey { get; set; }
        public List<string> AvailableDepartments { get; set; } = new();
        public string? Message { get; set; }
    }
}
