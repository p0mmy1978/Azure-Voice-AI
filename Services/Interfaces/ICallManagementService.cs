namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface ICallManagementService
    {
        /// <summary>
        /// Initialize the call management service with call automation client and connection tracking
        /// </summary>
        /// <param name="callAutomationClient">Azure Call Automation client</param>
        /// <param name="activeCallConnections">Dictionary tracking active connections</param>
        void Initialize(Azure.Communication.CallAutomation.CallAutomationClient callAutomationClient, Dictionary<string, string> activeCallConnections);

        /// <summary>
        /// Hang up the ACS call for the current caller
        /// </summary>
        /// <param name="callerId">Caller ID to hang up</param>
        /// <returns>True if call was hung up successfully, false otherwise</returns>
        Task<bool> HangUpCallAsync(string callerId);

        /// <summary>
        /// Check if a call is currently active for the given caller
        /// </summary>
        /// <param name="callerId">Caller ID to check</param>
        /// <returns>True if call is active, false otherwise</returns>
        bool IsCallActive(string callerId);

        /// <summary>
        /// Get the call connection ID for a given caller
        /// </summary>
        /// <param name="callerId">Caller ID</param>
        /// <returns>Call connection ID if found, null otherwise</returns>
        string? GetCallConnectionId(string callerId);
    }
}
