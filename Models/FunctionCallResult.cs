namespace CallAutomation.AzureAI.VoiceLive.Models
{
    public class FunctionCallResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public bool ShouldEndCall { get; set; } = false;
    }
}
