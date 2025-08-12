namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface IEmailService
    {
        /// <summary>
        /// Send an after-hours message email to a staff member
        /// </summary>
        /// <param name="recipientName">Name of the recipient</param>
        /// <param name="recipientEmail">Email address of the recipient</param>
        /// <param name="message">The message content</param>
        /// <param name="callerId">Caller's phone number</param>
        /// <returns>True if email sent successfully, false otherwise</returns>
        Task<bool> SendMessageEmailAsync(string recipientName, string recipientEmail, string message, string callerId);

        /// <summary>
        /// Initialize the Graph client (called once at startup)
        /// </summary>
        Task InitializeAsync();
    }
}
