using Azure.Data.Tables;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class StaffLookupService : IStaffLookupService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<StaffLookupService> _logger;

        public StaffLookupService(IConfiguration configuration, ILogger<StaffLookupService> logger)
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

        public async Task<StaffLookupResult> CheckStaffExistsAsync(string name, string? department = null)
        {
            try
            {
                var normalized = NormalizeName(name);
                _logger.LogInformation($"🔍 [StaffLookup] Checking staff: {name} (normalized: {normalized}), department: {department}");

                // If department is provided, try exact match first
                if (!string.IsNullOrWhiteSpace(department))
                {
                    return await CheckWithDepartment(normalized, name, department);
                }
                else
                {
                    return await CheckWithoutDepartment(normalized, name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error checking staff existence for: {name}");
                return new StaffLookupResult 
                { 
                    Status = StaffLookupStatus.NotFound,
                    Message = "Error occurred during lookup"
                };
            }
        }

        public async Task<string?> GetStaffEmailAsync(string name, string? department = null)
        {
            var result = await CheckStaffExistsAsync(name, department);
            return result.Status == StaffLookupStatus.Authorized ? result.Email : null;
        }

        private async Task<StaffLookupResult> CheckWithDepartment(string normalized, string originalName, string department)
        {
            var normalizedDept = department.Trim().ToLowerInvariant();
            var exactRowKey = $"{normalized}_{normalizedDept}";
            
            _logger.LogInformation($"🔍 [StaffLookup] Looking up with department: {exactRowKey}");
            
            var exactResult = await _tableClient.GetEntityIfExistsAsync<TableEntity>("staff", exactRowKey);
            
            if (exactResult.HasValue && exactResult.Value != null)
            {
                var entity = exactResult.Value;
                var email = GetEmailFromEntity(entity);
                
                if (IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Staff authorized: {originalName} in {department}, email: {email}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = exactRowKey
                    };
                }
                else
                {
                    _logger.LogWarning($"❌ Staff found but invalid email: {originalName} in {department}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                _logger.LogWarning($"❌ Staff NOT found: {originalName} in {department}");
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotAuthorized,
                    Message = "Staff member not found in specified department"
                };
            }
        }

        private async Task<StaffLookupResult> CheckWithoutDepartment(string normalized, string originalName)
        {
            _logger.LogInformation($"🔍 [StaffLookup] Searching for all matches of: {normalized}");
            
            // Query all entities that start with the normalized name
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'staff' and RowKey ge '{normalized}' and RowKey lt '{normalized}~'",
                maxPerPage: 10);
            
            var matches = new List<TableEntity>();
            await foreach (var entity in query)
            {
                // Check if RowKey starts with our normalized name
                if (entity.RowKey.StartsWith(normalized))
                {
                    matches.Add(entity);
                }
            }
            
            _logger.LogInformation($"🔍 Found {matches.Count} potential matches");
            
            if (matches.Count == 0)
            {
                _logger.LogWarning($"❌ No staff found matching: {originalName}");
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotFound,
                    Message = "No staff member found with that name"
                };
            }
            else if (matches.Count == 1)
            {
                var match = matches[0];
                var email = GetEmailFromEntity(match);
                
                if (IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Single match found and authorized: {originalName}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = match.RowKey
                    };
                }
                else
                {
                    _logger.LogWarning($"❌ Single match found but invalid email: {originalName}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                // Multiple matches found - need clarification
                var departments = matches.Where(m => m.ContainsKey("Department"))
                    .Select(m => m["Department"]?.ToString())
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct()
                    .ToList();
                
                _logger.LogInformation($"🟡 Multiple matches found for {originalName}. Departments: {string.Join(", ", departments)}");
                
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.MultipleFound,
                    AvailableDepartments = departments,
                    Message = $"Multiple staff members found with name '{originalName}'. Please specify department."
                };
            }
        }

        private static string NormalizeName(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "");
        }

        private static string? GetEmailFromEntity(TableEntity entity)
        {
            var emailObj = entity.ContainsKey("email") ? entity["email"] : null;
            return emailObj?.ToString()?.Trim();
        }

        private static bool IsValidEmail(string? email)
        {
            return !string.IsNullOrWhiteSpace(email) && email.Contains("@");
        }
    }
}
