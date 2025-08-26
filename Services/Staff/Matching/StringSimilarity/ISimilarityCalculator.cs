namespace CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity
{
    /// <summary>
    /// Interface for calculating similarity between two strings
    /// </summary>
    public interface ISimilarityCalculator
    {
        /// <summary>
        /// Calculate similarity between two strings
        /// </summary>
        /// <param name="source">First string to compare</param>
        /// <param name="target">Second string to compare</param>
        /// <returns>Similarity score between 0.0 (no match) and 1.0 (perfect match)</returns>
        double Calculate(string source, string target);

        /// <summary>
        /// Name of the similarity algorithm for logging/debugging
        /// </summary>
        string AlgorithmName { get; }

        /// <summary>
        /// Minimum threshold score to consider strings similar
        /// </summary>
        double MinimumThreshold { get; }
    }
}
