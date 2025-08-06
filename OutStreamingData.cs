namespace CallAutomation.AzureAI.VoiceLive
{
    public static class OutStreamingData
    {
        public static string GetAudioDataForOutbound(byte[] audioData)
        {
            // Convert audio data to the required format for ACS media streaming
            var jsonObject = new
            {
                kind = "AudioData",
                audioData = new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    data = Convert.ToBase64String(audioData),
                    format = "pcm16"
                }
            };
            return System.Text.Json.JsonSerializer.Serialize(jsonObject);
        }

        public static string GetStopAudioForOutbound()
        {
            // Send stop audio signal to ACS
            var jsonObject = new
            {
                kind = "StopAudio"
            };
            return System.Text.Json.JsonSerializer.Serialize(jsonObject);
        }
    }
}
