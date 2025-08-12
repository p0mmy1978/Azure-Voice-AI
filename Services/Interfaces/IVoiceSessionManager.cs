using System.Net.WebSockets;
using CallAutomation.AzureAI.VoiceLive.Models;

namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface IVoiceSessionManager
    {
        /// <summary>
        /// Connect to Azure Voice Live WebSocket
        /// </summary>
        /// <param name="endpoint">Azure Voice Live endpoint</param>
        /// <param name="apiKey">API key</param>
        /// <param name="model">Voice Live model</param>
        /// <returns>True if connected successfully</returns>
        Task<bool> ConnectAsync(string endpoint, string apiKey, string model);

        /// <summary>
        /// Update the AI session with configuration and tools
        /// </summary>
        /// <param name="config">Session configuration</param>
        /// <returns>True if updated successfully</returns>
        Task<bool> UpdateSessionAsync(SessionConfig config);

        /// <summary>
        /// Start the initial AI response
        /// </summary>
        /// <returns>True if started successfully</returns>
        Task<bool> StartResponseAsync();

        /// <summary>
        /// Send a message to the AI
        /// </summary>
        /// <param name="message">Message to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if sent successfully</returns>
        Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Receive a message from the AI
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Received message or null if none</returns>
        Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Check if the WebSocket connection is open
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Close the WebSocket connection
        /// </summary>
        /// <returns>True if closed successfully</returns>
        Task<bool> CloseAsync();
    }
}
