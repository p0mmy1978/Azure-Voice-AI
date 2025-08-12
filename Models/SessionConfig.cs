namespace CallAutomation.AzureAI.VoiceLive.Models
{
    public class SessionConfig
    {
        public string Instructions { get; set; } = string.Empty;
        public string VoiceName { get; set; } = "en-US-Emma:DragonHDLatestNeural";
        public double VoiceTemperature { get; set; } = 0.8;
        public double VadThreshold { get; set; } = 0.5;
        public int PrefixPaddingMs { get; set; } = 200;
        public int SilenceDurationMs { get; set; } = 200;
        public bool RemoveFillerWords { get; set; } = false;
    }
}
