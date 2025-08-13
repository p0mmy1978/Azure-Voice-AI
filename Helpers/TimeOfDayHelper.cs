namespace CallAutomation.AzureAI.VoiceLive.Helpers
{
    public static class TimeOfDayHelper
    {
        public static string GetGreeting()
        {
            var now = DateTime.Now;
            var hour = now.Hour;

            return hour switch
            {
                >= 5 and < 12 => "Good morning",
                >= 12 and < 17 => "Good afternoon", 
                >= 17 and < 21 => "Good evening",
                _ => "Good evening" // Late night/early morning
            };
        }

        public static string GetFarewell()
        {
            var now = DateTime.Now;
            var hour = now.Hour;

            return hour switch
            {
                >= 5 and < 17 => "Have a great day",
                >= 17 and < 21 => "Have a great evening",
                _ => "Have a great night"
            };
        }

        public static string GetTimeOfDay()
        {
            var now = DateTime.Now;
            var hour = now.Hour;

            return hour switch
            {
                >= 5 and < 12 => "morning",
                >= 12 and < 17 => "afternoon",
                >= 17 and < 21 => "evening", 
                _ => "night"
            };
        }
    }
}
