using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class AudioStreamProcessor : IAudioStreamProcessor
    {
        private readonly ILogger<AudioStreamProcessor> _logger;

        public AudioStreamProcessor(ILogger<AudioStreamProcessor> logger)
        {
            _logger = logger;
        }

        public async Task<bool> ProcessAudioDeltaAsync(string deltaData, AcsMediaStreamingHandler mediaStreaming)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deltaData))
                {
                    _logger.LogWarning("🔊 Received empty audio delta data");
                    return false;
                }

                _logger.LogDebug("🔊 Processing audio delta from AI");
                
                // Convert base64 delta to audio data for outbound streaming
                var audioBytes = Convert.FromBase64String(deltaData);
                var jsonString = OutStreamingData.GetAudioDataForOutbound(audioBytes);
                
                await mediaStreaming.SendMessageAsync(jsonString);
                
                _logger.LogDebug($"🔊 Audio delta processed successfully, sent {audioBytes.Length} bytes");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Failed to process audio delta");
                return false;
            }
        }

        public async Task<bool> HandleVoiceActivityAsync(bool isStarted, AcsMediaStreamingHandler mediaStreaming)
        {
            try
            {
                if (isStarted)
                {
                    _logger.LogInformation("🎤 Voice activity detection started - stopping AI audio output");
                    
                    // Send stop audio signal when user starts speaking
                    var jsonString = OutStreamingData.GetStopAudioForOutbound();
                    await mediaStreaming.SendMessageAsync(jsonString);
                    
                    _logger.LogDebug("🎤 Stop audio signal sent successfully");
                }
                else
                {
                    _logger.LogInformation("🎤 Voice activity detection stopped");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Failed to handle voice activity (started: {isStarted})");
                return false;
            }
        }

        public async Task<bool> SendAudioToExternalAIAsync(byte[] audioData, Func<string, CancellationToken, Task> sendMessageCallback)
        {
            try
            {
                if (audioData == null || audioData.Length == 0)
                {
                    _logger.LogWarning("🎤 Received empty audio data to send to AI");
                    return false;
                }

                _logger.LogDebug($"🎤 Sending {audioData.Length} bytes of audio data to AI");
                
                // Convert audio data to base64 and create message for AI
                var audioBytes = Convert.ToBase64String(audioData);
                var jsonObject = new
                {
                    type = "input_audio_buffer.append",
                    audio = audioBytes
                };

                var message = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                await sendMessageCallback(message, CancellationToken.None);
                
                _logger.LogDebug("🎤 Audio data sent to AI successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Failed to send audio to external AI");
                return false;
            }
        }
    }
}
