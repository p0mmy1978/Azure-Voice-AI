using Azure.Data.Tables;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Staff;
using CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Staff
{
    /// <summary>
    /// Handles fuzzy matching logic for staff lookup
    /// </summary>
    public class FuzzyMatchingService
    {
        private readonly CompositeSimilarityMatcher _similarityMatcher;
        private readonly TableQueryService _tableQuery;
        private readonly ILogger<FuzzyMatchingService> _logger;
        private const double FuzzyMatchThreshold = 0.7;

        public FuzzyMatchingService(
            CompositeSimilarityMatcher similarityMatcher,
            TableQueryService tableQuery,
            ILogger<FuzzyMatchingService> logger)
        {
            _similarityMatcher = similarityMatcher;
            _tableQuery = tableQuery;
            _logger = logger;
        }

        /// <summary>
        /// Perform fuzzy matching for staff lookup
        /// </summary>
        public async Task<StaffLookupResult> TryFuzzyMatchAsync(string originalName, string? department)
        {
            try
            {
                _logger.LogInformation($"🎯 [FuzzyMatch] Starting fuzzy search for: {originalName}");
                
                // Load all staff for comparison
                var allStaff = await _tableQuery.LoadAllStaffAsync();
                var matches = FindPotentialMatches(originalName, allStaff);

                if (matches.Count == 0)
                {
                    _logger.LogInformation($"🎯 [FuzzyMatch] No fuzzy matches found for: {originalName}");
                    return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
                }

                // Sort by similarity score (highest first)
                matches.Sort((a, b) => b.Score.CompareTo(a.Score));
                
                return ProcessMatches(matches, originalName, department);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error during fuzzy matching for: {originalName}");
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }
        }

        private List<(double Score, TableEntity Entity, string MatchType)> FindPotentialMatches(
            string originalName, 
            List<(string RowKey, TableEntity Entity)> allStaff)
        {
            var matches = new List<(double Score, TableEntity Entity, string MatchType)>();

            foreach (var (rowKey, entity) in allStaff)
            {
                var nameFromRowKey = NameNormalizer.ExtractNameFromRowKey(rowKey);
                var result = _similarityMatcher.CalculateBestMatch(originalName, nameFromRowKey);

                if (result.Score > FuzzyMatchThreshold)
                {
                    matches.Add((result.Score, entity, result.Algorithm));
                    _logger.LogDebug($"🎯 [FuzzyMatch] Match: '{nameFromRowKey}' for '{originalName}' (Score: {result.Score:F2}, Method: {result.Algorithm})");
                }
            }

            return matches;
        }

        private StaffLookupResult ProcessMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName, 
            string? department)
        {
            // Filter by department if specified
            if (!string.IsNullOrWhiteSpace(department))
            {
                return ProcessDepartmentFilteredMatches(matches, originalName, department);
            }
            else
            {
                return ProcessUnfilteredMatches(matches, originalName);
            }
        }

        private StaffLookupResult ProcessDepartmentFilteredMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName, 
            string department)
        {
            var deptMatches = matches.Where(m => 
                m.Entity.ContainsKey("Department") && 
                string.Equals(m.Entity["Department"]?.ToString(), department, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (deptMatches.Count == 1)
            {
                return CreateMatchResult(deptMatches[0], originalName, department, deptMatches[0].Score < 1.0);
            }
            else if (deptMatches.Count > 1)
            {
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.MultipleFound,
                    Message = "Multiple similar staff members found in the specified department"
                };
            }

            return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
        }

        private StaffLookupResult ProcessUnfilteredMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName)
        {
            var bestMatch = matches[0];
            
            // Very high confidence for auto-approval without department
            if (bestMatch.Score >= 0.95) 
            {
                var entityDepartment = ExtractDepartmentFromEntity(bestMatch.Entity);
                return CreateMatchResult(bestMatch, originalName, entityDepartment, false);
            }
            // Good match but needs confirmation
            else if (bestMatch.Score > 0.75) 
            {
                var entityDepartment = ExtractDepartmentFromEntity(bestMatch.Entity);
                return CreateMatchResult(bestMatch, originalName, entityDepartment, true);
            }
            // Multiple good matches - ask for department
            else if (matches.Count > 1 && matches.Take(2).All(m => m.Score > FuzzyMatchThreshold))
            {
                var departments = TableQueryService.ExtractDepartments(matches.Take(3).Select(m => m.Entity).ToList());
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.MultipleFound,
                    AvailableDepartments = departments,
                    Message = $"Found multiple similar staff members for '{originalName}'. Please specify the department."
                };
            }

            return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
        }

        private StaffLookupResult CreateMatchResult(
            (double Score, TableEntity Entity, string MatchType) match, 
            string originalName, 
            string? department, 
            bool needsConfirmation)
        {
            var email = TableQueryService.GetEmailFromEntity(match.Entity);
            
            if (!TableQueryService.IsValidEmail(email))
            {
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }

            var matchedName = NameNormalizer.ExtractNameFromRowKey(match.Entity.RowKey);
            
            // Extract department from the entity if not provided
            var entityDepartment = department ?? ExtractDepartmentFromEntity(match.Entity);
            
            if (needsConfirmation)
            {
                _logger.LogInformation($"❓ [FuzzyMatch] Match needs confirmation: '{matchedName}' for '{originalName}' (Score: {match.Score:F2}) in {entityDepartment}");
                
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.ConfirmationNeeded,
                    Email = email,
                    RowKey = match.Entity.RowKey,
                    Message = $"confirm:{originalName}:{matchedName}:{entityDepartment}:{match.Score:F2}",
                    SuggestedName = matchedName,
                    SuggestedDepartment = entityDepartment,
                    ConfidenceScore = match.Score
                };
            }
            
            _logger.LogInformation($"✅ [FuzzyMatch] High-confidence match: '{matchedName}' for '{originalName}' (Score: {match.Score:F2}, Method: {match.MatchType}) in {entityDepartment}");
            
            return new StaffLookupResult
            {
                Status = StaffLookupStatus.Authorized,
                Email = email,
                RowKey = match.Entity.RowKey,
                Message = $"Found staff member '{matchedName}' in {entityDepartment}",
                SuggestedName = matchedName,
                SuggestedDepartment = entityDepartment,
                ConfidenceScore = match.Score
            };
        }

        /// <summary>
        /// Extract department from table entity
        /// </summary>
        private string? ExtractDepartmentFromEntity(TableEntity entity)
        {
            if (entity.ContainsKey("Department"))
            {
                return entity["Department"]?.ToString()?.Trim();
            }

            // Try to extract from RowKey if Department field is missing
            // RowKey format: "name_department"
            var rowKey = entity.RowKey;
            var lastUnderscoreIndex = rowKey.LastIndexOf('_');
            if (lastUnderscoreIndex > 0 && lastUnderscoreIndex < rowKey.Length - 1)
            {
                var departmentFromRowKey = rowKey.Substring(lastUnderscoreIndex + 1);
                _logger.LogDebug($"🔍 [FuzzyMatch] Extracted department from RowKey: {departmentFromRowKey}");
                return departmentFromRowKey;
            }

            _logger.LogDebug($"🔍 [FuzzyMatch] No department found for entity with RowKey: {rowKey}");
            return null;
        }
    }
}
