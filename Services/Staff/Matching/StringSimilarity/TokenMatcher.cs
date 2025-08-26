namespace CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity
{
    /// <summary>
    /// Calculates similarity by comparing individual words/tokens
    /// Good for matching multi-word names where word order might vary
    /// </summary>
    public class TokenMatcher : ISimilarityCalculator
    {
        private readonly EditDistanceMatcher _editDistanceMatcher;

        public string AlgorithmName => "Token";
        public double MinimumThreshold => 0.6; // 60% token similarity threshold

        public TokenMatcher()
        {
            _editDistanceMatcher = new EditDistanceMatcher();
        }

        public double Calculate(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return 0.0;

            var tokens1 = source.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tokens2 = target.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (tokens1.Length == 0 || tokens2.Length == 0) 
                return 0.0;

            // Count matching tokens (with fuzzy matching)
            var matches = 0;
            var processedTokens2 = new HashSet<int>();

            foreach (var token1 in tokens1)
            {
                for (int i = 0; i < tokens2.Length; i++)
                {
                    if (processedTokens2.Contains(i))
                        continue;

                    var token2 = tokens2[i];
                    
                    // Check for exact match first
                    if (token1 == token2)
                    {
                        matches++;
                        processedTokens2.Add(i);
                        break;
                    }
                    
                    // Check for fuzzy match using edit distance
                    var similarity = _editDistanceMatcher.Calculate(token1, token2);
                    if (similarity > 0.8) // High threshold for individual tokens
                    {
                        matches++;
                        processedTokens2.Add(i);
                        break;
                    }
                }
            }
            
            // Calculate similarity as percentage of matched tokens
            var maxTokens = Math.Max(tokens1.Length, tokens2.Length);
            return (double)matches / maxTokens;
        }
    }
}
