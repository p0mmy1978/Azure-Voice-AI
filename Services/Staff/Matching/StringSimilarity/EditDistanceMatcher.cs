namespace CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity
{
    /// <summary>
    /// Calculates similarity using Levenshtein edit distance
    /// Good for matching names with minor spelling differences or typos
    /// </summary>
    public class EditDistanceMatcher : ISimilarityCalculator
    {
        public string AlgorithmName => "EditDistance";
        public double MinimumThreshold => 0.7; // 70% similarity threshold

        public double Calculate(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return 0.0;

            var s1 = source.ToLowerInvariant().Replace(" ", "");
            var s2 = target.ToLowerInvariant().Replace(" ", "");
            
            // Perfect match
            if (s1 == s2)
                return 1.0;

            var distance = CalculateLevenshteinDistance(s1, s2);
            var maxLength = Math.Max(s1.Length, s2.Length);
            
            return maxLength == 0 ? 1.0 : 1.0 - (double)distance / maxLength;
        }

        /// <summary>
        /// Calculate Levenshtein distance between two strings
        /// </summary>
        private int CalculateLevenshteinDistance(string s1, string s2)
        {
            if (s1.Length == 0) return s2.Length;
            if (s2.Length == 0) return s1.Length;

            var dp = new int[s1.Length + 1, s2.Length + 1];
            
            // Initialize base cases
            for (int i = 0; i <= s1.Length; i++) 
                dp[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++) 
                dp[0, j] = j;
            
            // Fill the matrix
            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), 
                        dp[i - 1, j - 1] + cost);
                }
            }
            
            return dp[s1.Length, s2.Length];
        }
    }
}
