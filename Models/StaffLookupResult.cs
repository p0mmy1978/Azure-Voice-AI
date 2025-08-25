namespace CallAutomation.AzureAI.VoiceLive.Models
{
    public enum StaffLookupStatus
    {
        Authorized,
        NotAuthorized,
        MultipleFound,
        NotFound,
        ConfirmationNeeded  // NEW: Indicates fuzzy match needs user confirmation
    }

    public class StaffLookupResult
    {
        public StaffLookupStatus Status { get; set; }
        public string? Email { get; set; }
        public string? RowKey { get; set; }
        public List<string> AvailableDepartments { get; set; } = new();
        public string? Message { get; set; }
        
        // NEW: Properties for confirmation flow
        public string? OriginalName { get; set; }      // What the user said
        public string? SuggestedName { get; set; }     // What the system found
        public string? SuggestedDepartment { get; set; } // Department of suggested match
        public double? ConfidenceScore { get; set; }   // Fuzzy match confidence (0-1)
        
        // Helper method to parse confirmation message
        public static (string original, string suggested, string dept, double score) ParseConfirmationMessage(string message)
        {
            // Format: "confirm:originalName:suggestedName:department:score"
            var parts = message.Split(':');
            if (parts.Length >= 5 && parts[0] == "confirm")
            {
                double.TryParse(parts[4], out double score);
                return (parts[1], parts[2], parts[3], score);
            }
            return ("", "", "", 0.0);
        }
    }
}
