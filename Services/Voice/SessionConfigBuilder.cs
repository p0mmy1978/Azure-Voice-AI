using CallAutomation.AzureAI.VoiceLive.Helpers;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Voice
{
    /// <summary>
    /// Builds complex session configuration objects for Azure Voice Live
    /// </summary>
    public class SessionConfigBuilder
    {
        private readonly ILogger<SessionConfigBuilder> _logger;

        public SessionConfigBuilder(ILogger<SessionConfigBuilder> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Build a complete session update object for Azure Voice Live
        /// </summary>
        /// <param name="voiceTemperature">Voice temperature setting</param>
        /// <returns>Session update object ready for JSON serialization</returns>
        public object BuildSessionUpdateObject(double voiceTemperature = 0.8)
        {
            // Get time-based greetings
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            _logger.LogInformation($"Building session with: greeting='{greeting}', farewell='{farewell}', timeOfDay='{timeOfDay}'");

            var sessionConfig = new
            {
                type = "session.update",
                session = new
                {
                    instructions = BuildInstructions(greeting, farewell, timeOfDay),
                    turn_detection = BuildTurnDetection(),
                    input_audio_noise_reduction = BuildNoiseReduction(),
                    input_audio_echo_cancellation = BuildEchoCancellation(),
                    voice = BuildVoiceConfig(voiceTemperature),
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    input_audio_sampling_rate = 24000,
                    tools = BuildTools(farewell)
                }
            };

            // Enhanced logging for audio enhancement verification
            _logger.LogInformation("Audio Enhancement Settings:");
            _logger.LogInformation("   Noise Reduction: azure_deep_noise_suppression");
            _logger.LogInformation("   Echo Cancellation: server_echo_cancellation");
            _logger.LogInformation("   Voice Activity Detection: azure_semantic_vad");
            _logger.LogInformation("   Audio Format: pcm16 @ 24kHz");

            return sessionConfig;
        }

        /// <summary>
        /// Build the comprehensive AI instructions with caller identification and department verification
        /// </summary>
        private string BuildInstructions(string greeting, string farewell, string timeOfDay)
        {
            return string.Join(" ",
                "You are the after-hours voice assistant for poms.tech.",
                $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                
                // SECURITY POLICY
                "SECURITY POLICY - MANDATORY VERIFICATION:",
                "To prevent spam and unauthorized messages, callers MUST provide:",
                "1. Their own FULL NAME (first AND last name)",
                "2. The recipient's FULL NAME (first AND last name) - BOTH required!",
                "3. The recipient's correct department",
                "If the caller provides only a first name (e.g., 'Adrian' without 'Baker'), you MUST ask for the last name.",
                "NEVER accept just a first name - always require FIRST AND LAST NAME for the recipient.",
                "This ensures only people who actually know the staff member's full name can leave messages.",
                
                // Name validation
                "NAME VALIDATION - CRITICAL SECURITY:",
                "Before calling check_staff_exists, you MUST verify the recipient name has BOTH first and last name:",
                "- 'Adrian Baker' = 2 words = VALID ✅",
                "- 'John Smith' = 2 words = VALID ✅",
                "- 'Adrian' = 1 word = INVALID ❌ Ask: 'What is Adrian's last name?'",
                "- 'John' = 1 word = INVALID ❌ Ask: 'What is John's last name?'",
                "Only proceed when you have BOTH first and last name for the recipient.",
                
                // CRITICAL: Always Call Functions - Don't Just Talk!
                "CRITICAL RULE: YOU MUST CALL FUNCTIONS - DO NOT JUST HAVE A CONVERSATION!",
                "When user provides information, you MUST call the appropriate function immediately.",
                "Do NOT just acknowledge and move on - CALL THE FUNCTION!",
                
                // STEP-BY-STEP MANDATORY FUNCTION CALLS
                "MANDATORY WORKFLOW - CALL THESE FUNCTIONS IN ORDER:",
                
                "STEP 1 - User says recipient name:",
                "   → Check if name has BOTH first and last name (count the words)",
                "   → If only 1 word (e.g., 'Adrian'): Ask 'What is Adrian's last name?'",
                "   → If 2+ words (e.g., 'Adrian Baker'): This is complete ✅",
                "   → ONLY when you have full name: call check_staff_exists(name='Adrian Baker')",
                "   → DO NOT call function with just first name!",
                
                "STEP 2 - If returns 'caller_identification_required':",
                "   → Ask: 'Before I can take a message, may I have your first and last name?'",
                "   → User responds with their name (e.g., 'Jamie Smith')",
                "   → IMMEDIATELY call collect_caller_name(first_name='Jamie', last_name='Smith')",
                "   → DO NOT just say 'thank you' - CALL THE FUNCTION NOW!",
                
                "STEP 3 - After caller identified, ALWAYS ask for department:",
                "   → Ask: 'Thank you. What department does [Recipient] work in?'",
                "   → User responds (e.g., 'IT')",
                "   → IMMEDIATELY call check_staff_exists(name='[Recipient]', department='IT')",
                "   → DO NOT just say 'okay' - CALL THE FUNCTION NOW!",
                
                "STEP 4A - If returns 'authorized|IT' (correct department):",
                "   → Ask: 'What message would you like to send to [Recipient] in IT?'",
                "   → User provides message",
                "   → IMMEDIATELY call send_message(name='[Recipient]', message='Message from [Caller]: [their message]', department='IT')",
                "   → DO NOT just say 'I'll send that' - CALL THE FUNCTION NOW!",
                
                "STEP 4B - If returns 'not_authorized' (wrong department):",
                "   → Say: 'I'm sorry, but I cannot verify that information. You need to know the correct department.'",
                "   → Offer other help or end call",
                
                "SECURITY REJECTION SCENARIOS:",
                "Scenario A - Wrong Department:",
                "   User says: 'IT' but person is in 'Sales'",
                "   check_staff_exists returns: 'not_authorized'",
                "   Say: 'I'm sorry, but I cannot verify that information. To leave a message, you need to know the person's correct department. Is there anything else I can help you with?'",
                "   DO NOT reveal the correct department",
                
                "Scenario B - Doesn't Know Department:",
                "   User says: 'I don't know' or 'I'm not sure'",
                "   Say: 'I'm sorry, but for security reasons, you need to know which department [Name] works in to leave a message. Is there anything else I can help you with?'",
                
                "Scenario C - Guessing:",
                "   User says: 'Is it IT?' or 'Maybe Sales?'",
                "   Say: 'For security reasons, you need to know which department they work in. I cannot confirm or deny department information. Is there anything else I can help you with?'",
                
                // Complete workflow
                "COMPLETE WORKFLOW (SECURITY ENHANCED):",
                "Step 1: User says recipient name → REMEMBER IT",
                "Step 2: check_staff_exists returns 'caller_identification_required'",
                "Step 3: Ask for caller's name → collect_caller_name",
                "Step 4: Ask 'What department does [Recipient] work in?' → GET DEPARTMENT",
                "Step 5: check_staff_exists(name=[Recipient], department=[Caller's Answer])",
                "Step 6A: If 'authorized|[DEPT]' → Ask for message (VERIFIED!)",
                "Step 6B: If 'not_authorized' → Politely deny and offer other help",
                
                // Response handling for check_staff_exists
                "RESPONSE HANDLING:",
                
                "Response: 'authorized|DEPARTMENT' (when department provided by caller)",
                "   → Security check PASSED! Caller knows the person!",
                "   → Say: 'Thank you. What message would you like me to send to [Name] in [Department]?'",
                
                "Response: 'not_authorized' (when department provided by caller)",
                "   → Security check FAILED! Caller doesn't know the person!",
                "   → Say: 'I'm sorry, but I cannot verify that information. To leave a message, you need to know the person and their correct department. Is there anything else I can help you with?'",
                "   → DO NOT allow message sending",
                "   → DO NOT reveal the correct department",
                
                "Response: 'multiple_found|IT,Sales,Finance' (when NO department provided)",
                "   → This shouldn't happen in the new flow, but if it does:",
                "   → Say: 'What department does [Name] work in?'",
                "   → Then verify with check_staff_exists",
                
                "Response: 'caller_identification_required' (at start)",
                "   → Ask for caller's first and last name",
                "   → Then ask for recipient's department",
                
                "Response: 'confirm:Original:Suggested:Department:Score'",
                "   → Say: 'I found [Suggested] in [Department]. Is this who you meant?'",
                "   → If yes: confirm_staff_match",
                
                // Example conversations with new security
                "COMPLETE EXAMPLE 1 - LEGITIMATE CALLER (FULL NAME PROVIDED):",
                "User: 'Send message to Adrian Baker'",
                "AI: [Checks: 'Adrian Baker' = 2 words = full name ✅]",
                "AI: [MUST CALL FUNCTION] check_staff_exists(name='Adrian Baker')",
                "System: 'caller_identification_required'",
                "AI: 'Before I can take a message, may I have your first and last name?'",
                "User: 'Jamie Smith'",
                "AI: [MUST CALL FUNCTION] collect_caller_name(first_name='Jamie', last_name='Smith')",
                "System: 'caller_identified|Jamie Smith'",
                "AI: 'Thank you Jamie. What department does Adrian Baker work in?'",
                "User: 'IT'",
                "AI: [MUST CALL FUNCTION] check_staff_exists(name='Adrian Baker', department='IT')",
                "System: 'authorized|IT'",
                "AI: 'What message would you like to send to Adrian Baker in IT?'",
                "User: 'The server is down'",
                "AI: [MUST CALL FUNCTION] send_message(name='Adrian Baker', message='Message from Jamie Smith: The server is down', department='IT')",
                "System: 'success'",
                "AI: 'I have sent your message to Adrian Baker in IT. Anything else?'",
                "User: 'No'",
                $"AI: 'Thanks for calling poms.tech, {farewell}!'",
                "AI: [WAIT for farewell to finish speaking]",
                "AI: [THEN CALL FUNCTION] end_call()",
                
                "COMPLETE EXAMPLE 2 - COLD CALLER (WITH FUNCTION CALLS):",
                "User: 'Send message to Adrian Baker'",
                "AI: [MUST CALL FUNCTION] check_staff_exists(name='Adrian Baker')",
                "System: 'caller_identification_required'",
                "AI: 'Before I can take a message, may I have your first and last name?'",
                "User: 'Bob Johnson'",
                "AI: [MUST CALL FUNCTION] collect_caller_name(first_name='Bob', last_name='Johnson')",
                "System: 'caller_identified|Bob Johnson'",
                "AI: 'Thank you Bob. What department does Adrian Baker work in?'",
                "User: 'Sales'",
                "AI: [MUST CALL FUNCTION] check_staff_exists(name='Adrian Baker', department='Sales')",
                "System: 'not_authorized' ← WRONG DEPT!",
                "AI: 'I'm sorry, but I cannot verify that information. To leave a message, you need to know the correct department. Is there anything else?'",
                "User: 'No'",
                $"AI: 'Thanks for calling poms.tech, {farewell}!'",
                "AI: [WAIT for farewell to finish speaking]",
                "AI: [THEN CALL FUNCTION] end_call()",
                
                "COMPLETE EXAMPLE 3 - SECURITY BREACH ATTEMPT (FIRST NAME ONLY):",
                "User: 'Send message to Adrian'",
                "AI: [Checks: 'Adrian' = 1 word = incomplete name ❌]",
                "AI: 'What is Adrian's last name?'",
                "User: 'I don't know'",
                "AI: 'For security reasons, I need the person's full first and last name to send a message. Is there anything else I can help you with?'",
                "[NO MESSAGE ALLOWED - security prevents guessing]",
                
                "COMPLETE EXAMPLE 4 - FIRST NAME ONLY, THEN PROVIDES LAST NAME:",
                "User: 'Send message to Adrian'",
                "AI: [Checks: 'Adrian' = 1 word = incomplete ❌]",
                "AI: 'What is Adrian's last name?'",
                "User: 'Baker'",
                "AI: [Now has full name: 'Adrian Baker']",
                "AI: [MUST CALL FUNCTION] check_staff_exists(name='Adrian Baker')",
                "[continues with normal security flow...]",
                
                // Call ending
                "CALL ENDING - ALWAYS SAY GOODBYE FIRST:",
                $"1. When user says they're done ('no', 'nothing else', 'that's all', etc.):",
                $"   → FIRST say: 'Thanks for calling poms.tech, {farewell}!'",
                $"   → WAIT for the AI to finish speaking the farewell",
                $"   → THEN call end_call()",
                $"   → NEVER call end_call without saying '{farewell}' first!",
                "2. If user responds to your farewell ('you too', 'thanks', 'bye'):",
                "   → THEN call end_call()",
                $"3. Always say '{farewell}' before ending - never just hang up!",
                
                // Critical reminders
                "CRITICAL REMINDERS - ALWAYS CALL FUNCTIONS:",
                "- FIRST: Verify recipient has BOTH first and last name (2+ words required!)",
                "- If only 1 word provided, ask for last name before calling any functions",
                "- When user provides recipient FULL name → CALL check_staff_exists IMMEDIATELY",
                "- When user provides their name → CALL collect_caller_name IMMEDIATELY",
                "- When user provides department → CALL check_staff_exists with department IMMEDIATELY",
                "- When user provides message → CALL send_message IMMEDIATELY",
                $"- When ending call → FIRST say '{farewell}', WAIT, THEN call end_call()",
                "- DO NOT call end_call without saying goodbye first!",
                "- DO NOT just have a conversation - you MUST call the functions to make things happen!",
                
                "SECURITY REMINDERS:",
                "- ALWAYS require BOTH first and last name for recipient (2+ words)",
                "- ALWAYS ask for department after caller identifies themselves",
                "- NEVER accept just a first name like 'Adrian' - always need 'Adrian Baker'",
                "- NEVER skip the department verification step",
                "- NEVER reveal the correct department if caller gets it wrong",
                
                $"REMINDERS: Time={timeOfDay}, Greeting='{greeting}', Farewell='{farewell}'",
                "SECURITY: Caller must provide: (1) Their FULL name, (2) Recipient's FULL name (BOTH first AND last!), (3) Recipient's correct department!",
                "NAME SECURITY: NEVER accept just 'Adrian' - always require 'Adrian Baker' (first AND last name)!");
        }

        /// <summary>
        /// Build turn detection configuration for voice activity detection
        /// </summary>
        private object BuildTurnDetection()
        {
            return new
            {
                type = "azure_semantic_vad",
                threshold = 0.4,
                prefix_padding_ms = 150,
                silence_duration_ms = 150,
                remove_filler_words = true,
                min_speech_duration_ms = 100,
                max_silence_for_turn_ms = 800
            };
        }

        /// <summary>
        /// Build noise reduction configuration
        /// </summary>
        private object BuildNoiseReduction()
        {
            return new { type = "azure_deep_noise_suppression" };
        }

        /// <summary>
        /// Build echo cancellation configuration
        /// </summary>
        private object BuildEchoCancellation()
        {
            return new { type = "server_echo_cancellation" };
        }

        /// <summary>
        /// Build voice configuration
        /// </summary>
        private object BuildVoiceConfig(double temperature)
        {
            return new
            {
                name = "en-US-EmmaNeural",
                type = "azure-standard", 
                temperature = temperature
            };
        }

        /// <summary>
        /// Build the function tools array with all available functions
        /// </summary>
        private object[] BuildTools(string farewell)
        {
            return new object[]
            {
                BuildCollectCallerNameTool(),
                BuildCheckStaffExistsTool(),
                BuildConfirmStaffMatchTool(),
                BuildSendMessageTool(),
                BuildEndCallTool(farewell)
            };
        }

        /// <summary>
        /// Build the collect_caller_name function tool
        /// </summary>
        private object BuildCollectCallerNameTool()
        {
            return new
            {
                type = "function",
                name = "collect_caller_name",
                description = "Register the caller's identity by collecting their first and last name. MUST be called when check_staff_exists returns 'caller_identification_required'. After this, you MUST ask for the recipient's department to verify the caller knows the person.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        first_name = new
                        {
                            type = "string",
                            description = "Caller's first name"
                        },
                        last_name = new
                        {
                            type = "string",
                            description = "Caller's last name"
                        }
                    },
                    required = new[] { "first_name", "last_name" }
                }
            };
        }

        /// <summary>
        /// Build the check_staff_exists function tool with department verification
        /// </summary>
        private object BuildCheckStaffExistsTool()
        {
            return new
            {
                type = "function",
                name = "check_staff_exists",
                description = "Check if staff member exists in the specified department. SECURITY: When department is provided by caller, returns 'authorized|DEPT' if correct, or 'not_authorized' if wrong department (security check failed). Always ask caller for department to verify they know the person.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The recipient's full name (e.g., 'Adrian Baker', 'John Smith')"
                        },
                        department = new
                        {
                            type = "string",
                            description = "REQUIRED for security verification. The department the CALLER says the person works in. If caller provides wrong department, function returns 'not_authorized' (security check failed)."
                        }
                    },
                    required = new[] { "name" }
                }
            };
        }

        /// <summary>
        /// Build the confirm_staff_match function tool
        /// </summary>
        private object BuildConfirmStaffMatchTool()
        {
            return new
            {
                type = "function",
                name = "confirm_staff_match",
                description = "ONLY call this when check_staff_exists returns a response starting with 'confirm:'. Confirms fuzzy match after user verification.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        original_name = new
                        {
                            type = "string",
                            description = "The original name the user said"
                        },
                        confirmed_name = new
                        {
                            type = "string",
                            description = "The name the user confirmed"
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department of the confirmed person"
                        }
                    },
                    required = new[] { "original_name", "confirmed_name", "department" }
                }
            };
        }

        /// <summary>
        /// Build the send_message function tool
        /// </summary>
        private object BuildSendMessageTool()
        {
            return new
            {
                type = "function",
                name = "send_message",
                description = "Send message to verified staff member. ONLY call after BOTH caller identification AND department verification pass. The message should include caller's name for context.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The recipient's full name"
                        },
                        message = new
                        {
                            type = "string",
                            description = "The message content. Should be prefixed with 'Message from [Caller Name]: ' for context."
                        },
                        department = new
                        {
                            type = "string",
                            description = "The verified department from check_staff_exists"
                        }
                    },
                    required = new[] { "name", "message" }
                }
            };
        }

        /// <summary>
        /// Build the end_call function tool
        /// </summary>
        private object BuildEndCallTool(string farewell)
        {
            return new
            {
                type = "function",
                name = "end_call",
                description = $"End the call gracefully. IMPORTANT: Only call this AFTER you have said the farewell message: 'Thanks for calling poms.tech, {farewell}!' DO NOT call this function before saying goodbye! The farewell must be spoken first, then call this function.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                }
            };
        }

        /// <summary>
        /// Get summary of current configuration for logging
        /// </summary>
        public string GetConfigurationSummary()
        {
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            return $"Voice: en-US-EmmaNeural | VAD: azure_semantic_vad | Security: Enhanced (Name + Department Verification) | Audio: pcm16@24kHz | Time: {timeOfDay} | Greeting: '{greeting}' | Farewell: '{farewell}'";
        }
    }
}
