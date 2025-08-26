namespace CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity
{
    /// <summary>
    /// Calculates phonetic similarity using a simplified Soundex-like algorithm
    /// Good for matching names that sound similar but are spelled differently
    /// </summary>
    public class PhoneticMatcher : ISimilarityCalculator
    {
        public string AlgorithmName => "Phonetic";
        public double MinimumThreshold => 1.0; // Soundex is binary - either matches or doesn't

        public double Calculate(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return 0.0;

            var soundex1 = GetSimpleSoundex(source);
            var soundex2 = GetSimpleSoundex(target);
            
            return soundex1 == soundex2 ? 1.0 : 0.0;
        }

        /// <summary>
        /// Generate a simplified Soundex code for phonetic matching
        /// </summary>
        private string GetSimpleSoundex(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) 
                return "";
            
            var normalized = input.ToLowerInvariant().Replace(" ", "");
            var soundex = new System.Text.StringBuilder();
            
            // Keep first letter
            soundex.Append(char.ToUpperInvariant(normalized[0]));
            
            // Simple consonant mapping (simplified Soundex)
            var consonantMap = new Dictionary<char, char>
            {
                ['b'] = '1', ['f'] = '1', ['p'] = '1', ['v'] = '1',
                ['c'] = '2', ['g'] = '2', ['j'] = '2', ['k'] = '2', 
                ['q'] = '2', ['s'] = '2', ['x'] = '2', ['z'] = '2',
                ['d'] = '3', ['t'] = '3',
                ['l'] = '4',
                ['m'] = '5', ['n'] = '5',
                ['r'] = '6'
            };
            
            for (int i = 1; i < normalized.Length && soundex.Length < 4; i++)
            {
                if (consonantMap.TryGetValue(normalized[i], out var code))
                {
                    if (soundex.Length == 1 || soundex[soundex.Length - 1] != code)
                    {
                        soundex.Append(code);
                    }
                }
            }
            
            // Pad with zeros
            while (soundex.Length < 4)
            {
                soundex.Append('0');
            }
            
            return soundex.ToString();
        }
    }
}
