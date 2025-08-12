using CallAutomation.AzureAI.VoiceLive.Models;

namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    public interface IStaffLookupService
    {
        /// <summary>
        /// Check if a staff member exists and is authorized to receive messages
        /// </summary>
        /// <param name="name">Staff member name</param>
        /// <param name="department">Optional department filter</param>
        /// <returns>Lookup result with status and details</returns>
        Task<StaffLookupResult> CheckStaffExistsAsync(string name, string? department = null);

        /// <summary>
        /// Get staff member's email address for messaging
        /// </summary>
        /// <param name="name">Staff member name</param>
        /// <param name="department">Optional department filter</param>
        /// <returns>Email address if found and valid</returns>
        Task<string?> GetStaffEmailAsync(string name, string? department = null);
    }
}
