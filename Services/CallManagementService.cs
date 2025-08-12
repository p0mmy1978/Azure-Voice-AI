using Azure.Communication.CallAutomation;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class CallManagementService : ICallManagementService
    {
        private readonly ILogger<CallManagementService> _logger;
        private CallAutomationClient _callAutomationClient = default!;
        private Dictionary<string, string> _activeCallConnections = default!;
        private readonly HashSet<string> _hungUpCalls = new();

        public CallManagementService(ILogger<CallManagementService> logger)
        {
            _logger = logger;
        }

        public void Initialize(CallAutomationClient callAutomationClient, Dictionary<string, string> activeCallConnections)
        {
            _callAutomationClient = callAutomationClient ?? throw new ArgumentNullException(nameof(callAutomationClient));
            _activeCallConnections = activeCallConnections ?? throw new ArgumentNullException(nameof(activeCallConnections));
            
            _logger.LogInformation("📞 CallManagementService initialized successfully");
        }

        public async Task<bool> HangUpCallAsync(string callerId)
        {
            if (_callAutomationClient == null || _activeCallConnections == null)
            {
                _logger.LogError("❌ CallManagementService not initialized");
                return false;
            }

            // Prevent multiple hangups for the same caller
            if (_hungUpCalls.Contains(callerId))
            {
                _logger.LogInformation($"📞 Call already hung up for caller: {callerId}");
                return true;
            }

            try
            {
                var callConnectionId = GetCallConnectionId(callerId);
                
                if (!string.IsNullOrEmpty(callConnectionId))
                {
                    _logger.LogInformation($"📞 Hanging up ACS call with CallConnectionId: {callConnectionId} for caller: {callerId}");
                    
                    var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);
                    await callConnection.HangUpAsync(forEveryone: true);
                    
                    // Mark this call as hung up
                    _hungUpCalls.Add(callerId);
                    
                    _logger.LogInformation($"✅ Successfully hung up ACS call: {callConnectionId} for caller: {callerId}");
                    return true;
                }
                else
                {
                    _logger.LogWarning($"⚠️ No CallConnectionId found to hang up for caller: {callerId}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to hang up ACS call for caller: {callerId}");
                return false;
            }
        }

        public bool IsCallActive(string callerId)
        {
            if (_activeCallConnections == null)
            {
                return false;
            }

            // Check if we have an active connection and haven't hung up yet
            var hasActiveConnection = _activeCallConnections.Values.Any();
            var isHungUp = _hungUpCalls.Contains(callerId);
            
            return hasActiveConnection && !isHungUp;
        }

        public string? GetCallConnectionId(string callerId)
        {
            if (_activeCallConnections == null)
            {
                return null;
            }

            // For now, return the first available connection ID
            // In a more complex scenario, you might need to map callerIds to specific connection IDs
            return _activeCallConnections.Values.FirstOrDefault();
        }
    }
}
