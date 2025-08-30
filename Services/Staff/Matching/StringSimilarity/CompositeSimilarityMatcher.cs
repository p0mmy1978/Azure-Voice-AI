using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity
{
    /// <summary>
    /// Composite matcher that uses multiple similarity algorithms and returns the best result
    /// </summary>
    public class CompositeSimilarityMatcher
    {
        private readonly List<ISimilarityCalculator> _calculators;
        private readonly ILogger<CompositeSimilarityMatcher> _logger;

        public CompositeSimilarityMatcher(ILogger<CompositeSimilarityMatcher> logger)
        {
            _logger = logger;
            _calculators = new List<ISimilarityCalculator>
            {
                new PhoneticMatcher(),
                new EditDistanceMatcher(), 
                new TokenMatcher()
            };
        }

        /// <summary>
        /// Calculate similarity using all available algorithms and return the best match
        /// </summary>
        /// <param name="source">Source string</param>
        /// <param name="target">Target string</param>
        /// <returns>Best similarity result with score and algorithm used</returns>
        public SimilarityResult CalculateBestMatch(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                return new SimilarityResult(0.0, "None", "Empty input");
            }

            var bestScore = 0.0;
            var bestAlgorithm = "None";
            var results = new List<(double Score, string Algorithm)>();

            foreach (var calculator in _calculators)
            {
                try
                {
                    var score = calculator.Calculate(source, target);
                    results.Add((score, calculator.AlgorithmName));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAlgorithm = calculator.AlgorithmName;
                    }

                    _logger.LogDebug($"🔍 [{calculator.AlgorithmName}] '{source}' vs '{target}' = {score:F3}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"⚠️ Error in {calculator.AlgorithmName} similarity calculation");
                }
            }

            var resultSummary = string.Join(", ", results.Select(r => $"{r.Algorithm}:{r.Score:F2}"));
            _logger.LogDebug($"🎯 Best match: {bestAlgorithm} with score {bestScore:F3} | All: [{resultSummary}]");

            return new SimilarityResult(bestScore, bestAlgorithm, resultSummary);
        }

        /// <summary>
        /// Check if any algorithm considers the strings similar enough
        /// </summary>
        /// <param name="source">Source string</param>
        /// <param name="target">Target string</param>
        /// <param name="overrideThreshold">Optional threshold override</param>
        /// <returns>True if any algorithm considers them similar</returns>
        public bool AreSimilar(string source, string target, double? overrideThreshold = null)
        {
            foreach (var calculator in _calculators)
            {
                var score = calculator.Calculate(source, target);
                var threshold = overrideThreshold ?? calculator.MinimumThreshold;
                
                if (score >= threshold)
                {
                    _logger.LogDebug($"✅ Similarity found via {calculator.AlgorithmName}: {score:F3} >= {threshold:F3}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get all similarity scores for debugging purposes
        /// </summary>
        public Dictionary<string, double> GetAllScores(string source, string target)
        {
            var scores = new Dictionary<string, double>();
            
            foreach (var calculator in _calculators)
            {
                try
                {
                    scores[calculator.AlgorithmName] = calculator.Calculate(source, target);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error calculating {calculator.AlgorithmName} score");
                    scores[calculator.AlgorithmName] = 0.0;
                }
            }

            return scores;
        }
    }

    /// <summary>
    /// Result of a similarity calculation with metadata
    /// </summary>
    public class SimilarityResult
    {
        public double Score { get; }
        public string Algorithm { get; }
        public string Details { get; }

        public SimilarityResult(double score, string algorithm, string details)
        {
            Score = score;
            Algorithm = algorithm;
            Details = details;
        }

        public bool IsMatch(double threshold = 0.7) => Score >= threshold;

        public override string ToString() => $"{Algorithm}: {Score:F3} ({Details})";
    }
}
