using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Helpers;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Voice
{
    /// <summary>
    /// Handles processing of messages from Azure Voice Live
    /// </summary>
    public class MessageProcessor
    {
        private readonly IFunctionCallProcessor _functionCallProcessor;
        private readonly IAudioStreamProcessor _audioStreamProcessor;
        private readonly IVoiceSessionManager _voiceSessionManager;
        private readonly ILogger<MessageProcessor> _logger;
        private readonly string _callerId;

        // Call ending state with enhanced tracking
        private bool _isEndingCall = false;
        private bool _goodbyeMessageStarted = false;
        private bool _farewellSent = false; // NEW: Track if farewell was actually sent
        private DateTime _goodbyeStartTime;
        private DateTime _farewellTime; // NEW: Track when farewell was sent

        public MessageProcessor(
            IFunctionCallProcessor functionCallProcessor,
            IAudioStreamProcessor audioStreamProcessor,
            IVoiceSessionManager voiceSessionManager,
            ILogger<MessageProcessor> logger,
            string callerId)
        {
            _functionCallProcessor = functionCallProcessor;
            _audioStreamProcessor = audioStreamProcessor;
            _voiceSessionManager = voiceSessionManager;
            _logger = logger;
            _callerId = callerId;
        }

        public bool IsEndingCall => _isEndingCall;

        /// <summary>
        /// Process a received message from Azure Voice Live
        /// </summary>
        public async Task<bool> ProcessMessageAsync(string receivedMessage, AcsMediaStreamingHandler mediaStreaming)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(receivedMessage))
                {
                    _logger.LogDebug("📭 Received empty message, skipping...");
                    return true;
                }

                _logger.LogInformation($"📥 Processing message: {receivedMessage.Length} chars");

                // Parse and validate JSON
                using var jsonDoc = ParseJson(receivedMessage);
                if (jsonDoc == null) return false;

                var root = jsonDoc.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    _logger.LogWarning("⚠️ Message missing 'type' property");
                    return false;
                }

                var messageType = typeElement.GetString();
                _logger.LogDebug($"🔄 Processing message type: {messageType}");

                return await RouteMessage(messageType!, root, mediaStreaming);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing message");
                return false;
            }
        }

        private JsonDocument? ParseJson(string message)
        {
            try
            {
                return JsonDocument.Parse(message);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, $"❌ Invalid JSON received: '{message}'. Skipping message.");
                return null;
            }
        }

        private async Task<bool> RouteMessage(string messageType, JsonElement root, AcsMediaStreamingHandler mediaStreaming)
        {
            switch (messageType)
            {
                case "response.audio.delta":
                    return await HandleAudioDelta(root, mediaStreaming);

                case "input_audio_buffer.speech_started":
                    return await HandleSpeechStarted(mediaStreaming);

                case "response.function_call_arguments.done":
                    return await HandleFunctionCall(root);

                case "response.output_item.added":
                    return HandleOutputItemAdded();

                case "response.done":
                case "response.audio.done":
                    return await HandleResponseCompletion();

                case "error":
                    return HandleError(root);

                case "session.created":
                case "session.updated":
                case "response.created":
                case "conversation.item.created":
                case "input_audio_buffer.speech_stopped":
                case "input_audio_buffer.committed":
                    return HandleStatusMessage(messageType);

                default:
                    _logger.LogDebug($"🔄 Unhandled message type: {messageType}");
                    return true;
            }
        }

        private async Task<bool> HandleAudioDelta(JsonElement root, AcsMediaStreamingHandler mediaStreaming)
        {
            try
            {
                if (root.TryGetProperty("delta", out var deltaProperty))
                {
                    var delta = deltaProperty.GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        await _audioStreamProcessor.ProcessAudioDeltaAsync(delta, mediaStreaming);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling audio delta");
                return false;
            }
        }

        private async Task<bool> HandleSpeechStarted(AcsMediaStreamingHandler mediaStreaming)
        {
            try
            {
                await _audioStreamProcessor.HandleVoiceActivityAsync(true, mediaStreaming);
                
                // ENHANCED: If user speaks after we've sent farewell, we should end the call immediately
                if (_farewellSent)
                {
                    _logger.LogInformation("🔚 User spoke after farewell was sent - ending call immediately to prevent loop");
                    _isEndingCall = true;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling speech started");
                return false;
            }
        }

        private async Task<bool> HandleFunctionCall(JsonElement root)
        {
            try
            {
                var functionName = root.GetProperty("name").GetString();
                var callId = root.GetProperty("call_id").GetString();
                var args = root.GetProperty("arguments").ToString();
                
                _logger.LogInformation($"🔧 Function call: {functionName}, Call ID: {callId}");

                var functionResult = await _functionCallProcessor.ProcessFunctionCallAsync(functionName!, args, callId!, _callerId);
                await _functionCallProcessor.SendFunctionResponseAsync(callId!, functionResult.Output, _voiceSessionManager.SendMessageAsync);
                
                if (functionResult.ShouldEndCall)
                {
                    _isEndingCall = true;
                    var farewell = TimeOfDayHelper.GetFarewell();
                    _logger.LogInformation($"🔚 Call ending requested - will use farewell: '{farewell}'");
                    
                    // ENHANCED: Set farewell time when end_call is triggered
                    _farewellTime = DateTime.Now;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling function call");
                return false;
            }
        }

        private bool HandleOutputItemAdded()
        {
            try
            {
                if (_isEndingCall && !_goodbyeMessageStarted)
                {
                    _goodbyeMessageStarted = true;
                    _farewellSent = true; // NEW: Track that farewell is being sent
                    _farewellTime = DateTime.Now; // NEW: Record farewell time
                    _goodbyeStartTime = DateTime.Now;
                    var farewell = TimeOfDayHelper.GetFarewell();
                    _logger.LogInformation($"🎤 Goodbye message started with farewell: '{farewell}' - call should end soon");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling output item");
                return false;
            }
        }

        private async Task<bool> HandleResponseCompletion()
        {
            try
            {
                if (_isEndingCall)
                {
                    // ENHANCED: If we've sent a farewell, wait for it to play completely
                    if (_farewellSent)
                    {
                        var timeSinceFarewell = DateTime.Now - _farewellTime;
                        _logger.LogInformation($"🔚 AI farewell completed - time since start: {timeSinceFarewell.TotalMilliseconds:F0}ms");
                        
                        // Calculate remaining time to let audio play
                        // A goodbye message typically takes 3-5 seconds to say
                        var minimumPlayTime = TimeSpan.FromSeconds(5); // Increased from 3
                        var remainingTime = minimumPlayTime - timeSinceFarewell;
                        
                        if (remainingTime.TotalMilliseconds > 0)
                        {
                            var delayMs = (int)remainingTime.TotalMilliseconds + 1000; // Extra 1 second buffer
                            _logger.LogInformation($"⏱️ Waiting {delayMs}ms for goodbye to finish playing");
                            await Task.Delay(delayMs);
                        }
                        else
                        {
                            _logger.LogInformation("⏱️ Goodbye should have finished playing, adding small buffer");
                            await Task.Delay(1000); // Small buffer even if time elapsed
                        }
                        
                        _logger.LogInformation("🔚 Ending call after goodbye completion");
                        return true; // Signal to end call
                    }
                    
                    // Fallback for cases where farewell tracking failed
                    _logger.LogInformation("🔚 AI response completed - ending call");
                    
                    var delay = CalculateGoodbyeDelay();
                    _logger.LogInformation($"⏱️ Waiting {delay}ms for goodbye to complete");
                    
                    await Task.Delay(delay);
                    
                    // Signal that call should be ended
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling response completion");
                return false;
            }
        }

        private bool HandleError(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("error", out var errorProperty))
                {
                    _logger.LogError($"❌ Azure Voice Live error: {errorProperty}");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling error message");
                return false;
            }
        }

        private bool HandleStatusMessage(string messageType)
        {
            _logger.LogDebug($"ℹ️ Status message: {messageType}");
            return true;
        }

        private int CalculateGoodbyeDelay()
        {
            try
            {
                // ENHANCED: If farewell was sent, use longer delay to ensure audio plays
                if (_farewellSent)
                {
                    var timeSinceFarewell = DateTime.Now - _farewellTime;
                    var minimumDuration = TimeSpan.FromSeconds(5); // Increased from 3
                    var remainingTime = minimumDuration - timeSinceFarewell;
                    
                    var delay = remainingTime.TotalMilliseconds > 0 
                        ? (int)remainingTime.TotalMilliseconds + 1000  // Add 1 second buffer
                        : 1000; // Minimum 1 second delay
                        
                    _logger.LogDebug($"🔚 Calculated farewell delay: {delay}ms (time since farewell: {timeSinceFarewell.TotalMilliseconds:F0}ms)");
                    return delay;
                }
                
                // Original logic for other cases
                if (_goodbyeMessageStarted)
                {
                    var elapsed = DateTime.Now - _goodbyeStartTime;
                    var estimatedDuration = TimeSpan.FromSeconds(6); // Increased from 5
                    var remaining = estimatedDuration - elapsed;
                    
                    return remaining.TotalMilliseconds > 0 
                        ? (int)remaining.TotalMilliseconds + 1500 // Increased buffer
                        : 1500; // Increased minimum
                }
                return 7000; // Increased default fallback from 6000
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error calculating goodbye delay");
                return 3000; // Increased error fallback
            }
        }

        /// <summary>
        /// Reset call ending state (for testing or error recovery)
        /// </summary>
        public void ResetCallEndingState()
        {
            _isEndingCall = false;
            _goodbyeMessageStarted = false;
            _farewellSent = false; // NEW
            _goodbyeStartTime = default;
            _farewellTime = default; // NEW
            _logger.LogDebug("🔄 Call ending state reset");
        }

        /// <summary>
        /// Get current call state for debugging
        /// </summary>
        public (bool IsEnding, bool GoodbyeStarted, bool FarewellSent, TimeSpan ElapsedGoodbye, TimeSpan ElapsedFarewell) GetCallState()
        {
            var elapsedGoodbye = _goodbyeMessageStarted ? DateTime.Now - _goodbyeStartTime : TimeSpan.Zero;
            var elapsedFarewell = _farewellSent ? DateTime.Now - _farewellTime : TimeSpan.Zero;
            
            return (_isEndingCall, _goodbyeMessageStarted, _farewellSent, elapsedGoodbye, elapsedFarewell);
        }

        /// <summary>
        /// Force end call state for emergency situations
        /// </summary>
        public void ForceEndCall()
        {
            _isEndingCall = true;
            _farewellSent = true;
            _farewellTime = DateTime.Now;
            _logger.LogWarning("⚠️ Force ending call - farewell bypass activated");
        }
    }
}
