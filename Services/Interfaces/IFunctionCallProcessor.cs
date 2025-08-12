using CallAutomation.AzureAI.VoiceLive.Models;

namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface IFunctionCallProcessor
    {
        /// <summary>
        /// Process a function call from the AI and execute the appropriate action
        /// </summary>
        /// <param name="functionName">Name of the function to execute</param>
        /// <param name="arguments">JSON arguments for the function</param>
        /// <param name="callId">Call ID for response tracking</param>
        /// <param name="callerId">Caller ID for context</param>
        /// <returns>Function call result</returns>
        Task<FunctionCallResult> ProcessFunctionCallAsync(string functionName, string arguments, string callId, string callerId);

        /// <summary>
        /// Send function response back to AI and trigger next response
        /// </summary>
        /// <param name="callId">Call ID</param>
        /// <param name="output">Function output</param>
        /// <param name="sendMessageCallback">Callback to send message to AI</param>
        /// <returns>True if sent successfully</returns>
        Task<bool> SendFunctionResponseAsync(string callId, string output, Func<string, CancellationToken, Task> sendMessageCallback);
    }
}
