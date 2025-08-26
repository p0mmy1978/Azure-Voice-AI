using Azure.Data.Tables;
using CallAutomation.AzureAI.VoiceLive.Services.Staff;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Staff
{
    /// <summary>
    /// Handles all table storage queries for staff lookup
    /// </summary>
    public class TableQueryService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableQueryService> _logger;

        public TableQueryService(IConfiguration configuration, ILogger<TableQueryService> logger)
        {
            _logger = logger;
            
            var tableServiceUri = new Uri(configuration["StorageUri"]!);
            _tableClient = new TableClient(
                tableServiceUri,
                configuration["TableName"]!,
                new TableSharedKeyCredential(
                    configuration["StorageAccountName"]!,
                    configuration["StorageAccountKey"]!));
        }

        /// <summary>
        /// Get staff member by exact name and department
        /// </summary>
        public async Task<TableEntity?> GetStaffByExactMatchAsync(string normalizedName, string department)
        {
            var exactRowKey = NameNormalizer.CreateRowKey(normalizedName, department);
            _logger.LogDebug($"🔍 [TableQuery] Exact lookup: {exactRowKey}");
            
            var result = await _tableClient.GetEntityIfExistsAsync<TableEntity>("staff", exactRowKey);
            return result.HasValue ? result.Value : null;
        }

        /// <summary>
        /// Find all staff members matching a normalized name
        /// </summary>
        public async Task<List<TableEntity>> FindAllMatchesAsync(string normalizedName)
        {
            _logger.LogDebug($"🔍 [TableQuery] Finding all matches for: {normalizedName}");
            
            var matches = new List<TableEntity>();
            
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'staff' and RowKey ge '{normalizedName}' and RowKey lt '{normalizedName}~'",
                maxPerPage: 10);
            
            await foreach (var entity in query)
            {
                if (entity.RowKey.StartsWith(normalizedName))
                {
                    matches.Add(entity);
                }
            }
            
            _logger.LogDebug($"🔍 [TableQuery] Found {matches.Count} matches for {normalizedName}");
            return matches;
        }

        /// <summary>
        /// Load all staff members for fuzzy matching
        /// </summary>
        public async Task<List<(string RowKey, TableEntity Entity)>> LoadAllStaffAsync()
        {
            _logger.LogDebug("🔍 [TableQuery] Loading all staff for fuzzy comparison");
            
            var allStaff = new List<(string RowKey, TableEntity Entity)>();
            
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'staff'",
                maxPerPage: 100);
            
            await foreach (var entity in query)
            {
                allStaff.Add((entity.RowKey, entity));
            }

            _logger.LogDebug($"🔍 [TableQuery] Loaded {allStaff.Count} staff members");
            return allStaff;
        }

        /// <summary>
        /// Get email from table entity
        /// </summary>
        public static string? GetEmailFromEntity(TableEntity entity)
        {
            var emailObj = entity.ContainsKey("email") ? entity["email"] : null;
            return emailObj?.ToString()?.Trim();
        }

        /// <summary>
        /// Check if email is valid
        /// </summary>
        public static bool IsValidEmail(string? email)
        {
            return !string.IsNullOrWhiteSpace(email) && email.Contains("@");
        }

        /// <summary>
        /// Extract departments from a list of entities
        /// </summary>
        public static List<string> ExtractDepartments(List<TableEntity> entities)
        {
            return entities.Where(m => m.ContainsKey("Department"))
                .Select(m => m["Department"]?.ToString())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList()!;
        }
    }
}
