using Azure.Communication.CallAutomation;
using System.Collections.Concurrent;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class CallManagementService : ICallManagementService
    {
        private readonly ILogger<CallManagementService> _logger;
        private CallAutomationClient _callAutomationClient = default!;
        private ConcurrentDictionary<string, string> _activeCallConnections = default!;
        private readonly HashSet<string> _hungUpCalls = new();

        public CallManagementService(ILogger<CallManagementService> logger)
        {
            _logger = logger;
        }

        public void Initialize(CallAutomationClient callAutomationClient, ConcurrentDictionary<string, string> activeCallConnections)
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
                    
                    _hungUpCalls.Add(callerId);
                    
                    bool removed = _activeCallConnections.Remove(callerId);
                    if (removed)
                    {
                        _logger.LogDebug($"🧹 Removed CallConnectionId from active connections for caller: {callerId}");
                    }
                    
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

            var hasActiveConnection = _activeCallConnections.ContainsKey(callerId);
            var isHungUp = _hungUpCalls.Contains(callerId);
            
            return hasActiveConnection && !isHungUp;
        }

        public string? GetCallConnectionId(string callerId)
        {
            if (_activeCallConnections == null)
            {
                return null;
            }

            _activeCallConnections.TryGetValue(callerId, out var connectionId);
            return connectionId;
        }
    }
}
