namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface IAudioStreamProcessor
    {
        /// <summary>
        /// Process incoming audio delta from AI and forward to media streaming
        /// </summary>
        /// <param name="deltaData">Base64 encoded audio delta</param>
        /// <param name="mediaStreaming">Media streaming handler</param>
        /// <returns>True if processed successfully</returns>
        Task<bool> ProcessAudioDeltaAsync(string deltaData, AcsMediaStreamingHandler mediaStreaming);

        /// <summary>
        /// Handle voice activity detection events
        /// </summary>
        /// <param name="isStarted">True if speech started, false if stopped</param>
        /// <param name="mediaStreaming">Media streaming handler</param>
        /// <returns>True if processed successfully</returns>
        Task<bool> HandleVoiceActivityAsync(bool isStarted, AcsMediaStreamingHandler mediaStreaming);

        /// <summary>
        /// Process outbound audio data to send to external AI
        /// </summary>
        /// <param name="audioData">Raw audio data</param>
        /// <param name="sendMessageCallback">Callback to send message to AI</param>
        /// <returns>True if processed successfully</returns>
        Task<bool> SendAudioToExternalAIAsync(byte[] audioData, Func<string, CancellationToken, Task> sendMessageCallback);
    }
}
