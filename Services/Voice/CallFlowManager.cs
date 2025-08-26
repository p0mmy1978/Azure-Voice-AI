using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Voice
{
    /// <summary>
    /// Manages call lifecycle and ending procedures
    /// </summary>
    public class CallFlowManager
    {
        private readonly ICallManagementService _callManagementService;
        private readonly IVoiceSessionManager _voiceSessionManager;
        private readonly ILogger<CallFlowManager> _logger;
        private readonly string _callerId;

        public CallFlowManager(
            ICallManagementService callManagementService,
            IVoiceSessionManager voiceSessionManager,
            ILogger<CallFlowManager> logger,
            string callerId)
        {
            _callManagementService = callManagementService;
            _voiceSessionManager = voiceSessionManager;
            _logger = logger;
            _callerId = callerId;
        }

        /// <summary>
        /// Handle call ending procedure
        /// </summary>
        public async Task<bool> EndCallAsync()
        {
            try
            {
                _logger.LogInformation($"🔚 Initiating call end procedure for: {_callerId}");
                
                // First hang up the ACS call
                var hangupSuccess = await _callManagementService.HangUpCallAsync(_callerId);
                if (!hangupSuccess)
                {
                    _logger.LogWarning($"⚠️ Failed to hang up ACS call for: {_callerId}");
                }
                
                // Then close the voice session
                var closeSuccess = await _voiceSessionManager.CloseAsync();
                if (!closeSuccess)
                {
                    _logger.LogWarning($"⚠️ Failed to close voice session for: {_callerId}");
                }
                
                var overallSuccess = hangupSuccess && closeSuccess;
                _logger.LogInformation($"{(overallSuccess ? "✅" : "⚠️")} Call end procedure completed for: {_callerId}");
                
                return overallSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error ending call for: {_callerId}");
                
                // Attempt cleanup even if there's an error
                try
                {
                    await _voiceSessionManager.CloseAsync();
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "❌ Error during cleanup after call end failure");
                }
                
                return false;
            }
        }

        /// <summary>
        /// Check if call is still active
        /// </summary>
        public bool IsCallActive()
        {
            try
            {
                return _callManagementService.IsCallActive(_callerId) && _voiceSessionManager.IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error checking call status for: {_callerId}");
                return false;
            }
        }

        /// <summary>
        /// Get call connection information for debugging
        /// </summary>
        public (string? ConnectionId, bool IsConnected, bool IsActive) GetCallInfo()
        {
            try
            {
                var connectionId = _callManagementService.GetCallConnectionId(_callerId);
                var isConnected = _voiceSessionManager.IsConnected;
                var isActive = _callManagementService.IsCallActive(_callerId);
                
                return (connectionId, isConnected, isActive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error getting call info for: {_callerId}");
                return (null, false, false);
            }
        }
    }
}
