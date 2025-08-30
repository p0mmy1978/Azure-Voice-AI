namespace CallAutomation.AzureAI.VoiceLive.Models
{
    public class SessionConfig
    {
        public string Instructions { get; set; } = string.Empty;
        public string VoiceName { get; set; } = "en-US-Emma:DragonHDLatestNeural";
        public double VoiceTemperature { get; set; } = 0.8;
        
        // Enhanced VAD settings for better speech recognition
        public double VadThreshold { get; set; } = 0.5;  // Lowered from 0.5 for more sensitivity
        public int PrefixPaddingMs { get; set; } = 200;  // Increased from 200ms for better speech capture
        public int SilenceDurationMs { get; set; } = 200; // Increased from 200ms for better speech boundary detection
        public bool RemoveFillerWords { get; set; } = true; // Changed to true to clean up transcription
        
        // Additional settings for better audio quality and speech recognition
        public bool UseDeepNoiseSuppression { get; set; } = true;
        public bool UseEchoCancellation { get; set; } = true;
        public string AudioFormat { get; set; } = "pcm16"; // Ensure consistent audio format
        
        // Advanced speech recognition settings
        public bool EnableBetterSpeechBoundaries { get; set; } = true;
        public int MaxSilenceBeforeStop { get; set; } = 1000; // Maximum silence before considering speech ended
        public double SpeechConfidenceThreshold { get; set; } = 0.7; // Minimum confidence for speech recognition
       
        public double SpeakingRate { get; set; } = 1.0; // Normal speaking rate
        public double VoicePitch { get; set; } = 0.0; // Normal pitch
        
        // Fuzzy matching awareness settings
        public bool EnableNameClarification { get; set; } = true; // Ask for clarification on unclear names
        public int MaxClarificationAttempts { get; set; } = 2; // How many times to ask for clarification
        public bool UseFuzzyMatching { get; set; } = true; // Indicates fuzzy matching is available
    }
}
