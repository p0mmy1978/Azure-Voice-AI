using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Staff
{
    /// <summary>
    /// Utility class for normalizing and processing names for staff lookup
    /// </summary>
    public static class NameNormalizer
    {
        /// <summary>
        /// Normalize a name for consistent lookup (lowercase, no spaces)
        /// </summary>
        /// <param name="name">Input name</param>
        /// <returns>Normalized name string</returns>
        public static string Normalize(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "");
        }

        /// <summary>
        /// Extract readable name from a RowKey format (firstnamelastname_department)
        /// </summary>
        /// <param name="rowKey">Table row key</param>
        /// <returns>Human-readable name with spaces</returns>
        public static string ExtractNameFromRowKey(string rowKey)
        {
            if (string.IsNullOrWhiteSpace(rowKey))
                return string.Empty;

            // RowKey format: "firstnamelastname_department" -> extract name part
            var underscoreIndex = rowKey.LastIndexOf('_');
            var namePart = underscoreIndex > 0 ? rowKey.Substring(0, underscoreIndex) : rowKey;
            
            // Try to reconstruct readable name by adding space before capital letters
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < namePart.Length; i++)
            {
                if (i > 0 && char.IsUpper(namePart[i]) && char.IsLower(namePart[i - 1]))
                {
                    result.Append(' ');
                }
                result.Append(namePart[i]);
            }
            
            return result.ToString();
        }

        /// <summary>
        /// Create a cache key for staff lookups
        /// </summary>
        /// <param name="normalizedName">Normalized name</param>
        /// <param name="department">Optional department</param>
        /// <returns>Cache key string</returns>
        public static string CreateCacheKey(string normalizedName, string? department = null)
        {
            return $"{normalizedName}_{department?.ToLowerInvariant() ?? ""}";
        }

        /// <summary>
        /// Create a row key for table storage lookup
        /// </summary>
        /// <param name="normalizedName">Normalized name</param>
        /// <param name="department">Department name</param>
        /// <returns>Row key string</returns>
        public static string CreateRowKey(string normalizedName, string department)
        {
            var normalizedDept = department.Trim().ToLowerInvariant();
            return $"{normalizedName}_{normalizedDept}";
        }

        /// <summary>
        /// Log common speech recognition errors and alternatives for debugging
        /// </summary>
        /// <param name="heardName">Name as heard by speech recognition</param>
        /// <param name="logger">Logger instance</param>
        public static void LogSpeechHints(string heardName, ILogger logger)
        {
            var commonMishearings = new Dictionary<string, string[]>
            {
                ["tock"] = ["tops", "talk", "tok", "tox"],
                ["tops"] = ["tock", "tops", "taps", "tips"],
                ["smith"] = ["smyth", "smythe", "schmith"],
                ["jon"] = ["john", "joan", "jean"],
                ["brian"] = ["bryan", "bryant"],
                ["mary"] = ["marie", "merry", "mary"],
                ["catherine"] = ["katherine", "kathryn", "katharine"],
                ["michael"] = ["michele", "mitchell"],
                ["terry"] = ["terri", "teri", "jerry"],
                ["chris"] = ["kris", "christine", "christopher"],
                ["lee"] = ["leigh", "li", "lea"],
                ["ann"] = ["anne", "anna", "annie"],
                ["john"] = ["jon", "jonathan", "johnny"]
            };

            var normalized = heardName.ToLowerInvariant();
            var alternatives = new List<string>();

            foreach (var (error, corrections) in commonMishearings)
            {
                if (normalized.Contains(error))
                {
                    foreach (var correction in corrections)
                    {
                        var alternative = normalized.Replace(error, correction);
                        alternatives.Add(alternative);
                    }
                }
            }

            if (alternatives.Any())
            {
                logger.LogInformation($"💡 [SpeechHints] For '{heardName}', consider these alternatives: {string.Join(", ", alternatives)}");
            }
        }

        /// <summary>
        /// Check if two names might be the same person with different spellings
        /// </summary>
        /// <param name="name1">First name</param>
        /// <param name="name2">Second name</param>
        /// <returns>True if they might be the same person</returns>
        public static bool MightBeSamePerson(string name1, string name2)
        {
            if (string.IsNullOrWhiteSpace(name1) || string.IsNullOrWhiteSpace(name2))
                return false;

            var normalized1 = Normalize(name1);
            var normalized2 = Normalize(name2);

            // Exact match after normalization
            if (normalized1 == normalized2)
                return true;

            // Check for common nickname patterns
            var nicknames = new Dictionary<string, string[]>
            {
                ["robert"] = ["rob", "bob", "robbie", "bobby"],
                ["william"] = ["will", "bill", "billy", "willie"],
                ["richard"] = ["rick", "dick", "richie", "ricky"],
                ["michael"] = ["mike", "mick", "mickey"],
                ["christopher"] = ["chris", "kit"],
                ["elizabeth"] = ["liz", "beth", "betsy", "lizzie"],
                ["katherine"] = ["kate", "katy", "katie", "kay"],
                ["margaret"] = ["meg", "maggie", "peggy", "marge"],
                ["patricia"] = ["pat", "patty", "trish"]
            };

            // Check if one name is a nickname of the other
            foreach (var (fullName, nicks) in nicknames)
            {
                if ((normalized1 == fullName && nicks.Contains(normalized2)) ||
                    (normalized2 == fullName && nicks.Contains(normalized1)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Split a full name into potential first and last name components
        /// </summary>
        /// <param name="fullName">Full name string</param>
        /// <returns>Tuple of (firstName, lastName, hasMiddle)</returns>
        public static (string FirstName, string LastName, bool HasMiddle) SplitName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return (string.Empty, string.Empty, false);

            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            return parts.Length switch
            {
                1 => (parts[0], string.Empty, false),
                2 => (parts[0], parts[1], false),
                >= 3 => (parts[0], parts[^1], true), // First and last, with middle names
                _ => (string.Empty, string.Empty, false)
            };
        }
    }
}
