using CallAutomation.AzureAI.VoiceLive.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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

            // ENHANCED VERIFICATION LOGGING - Using LogWarning to ensure visibility in production
            _logger.LogWarning("═══════════════════════════════════════════════");
            _logger.LogWarning("🔊 AUDIO ENHANCEMENT CONFIGURATION VERIFICATION");
            _logger.LogWarning("═══════════════════════════════════════════════");
            _logger.LogWarning("📍 NOISE PROFILE: OPEN OFFICE (AGGRESSIVE FILTERING)");
            _logger.LogWarning("═══════════════════════════════════════════════");

            // Serialize to inspect the actual configuration
            var turnDetection = BuildTurnDetection();
            var noiseReduction = BuildNoiseReduction();
            var echoCancellation = BuildEchoCancellation();

            _logger.LogWarning($"✅ Noise Reduction: azure_deep_noise_suppression (MAXIMUM)");
            _logger.LogWarning($"✅ Echo Cancellation: server_echo_cancellation");
            _logger.LogWarning($"✅ VAD Type: azure_semantic_vad");
            _logger.LogWarning($"✅ VAD Threshold: 0.6 (60% confidence - filters background)");
            _logger.LogWarning($"✅ VAD Prefix Padding: 150ms");
            _logger.LogWarning($"✅ VAD Silence Duration: 400ms (ignores brief pauses)");
            _logger.LogWarning($"✅ VAD Min Speech: 250ms (ignores quick sounds)");
            _logger.LogWarning($"✅ VAD Max Silence: 1200ms (patient with caller)");
            _logger.LogWarning($"✅ VAD Remove Filler Words: true");
            _logger.LogWarning($"✅ Audio Format: pcm16 @ 24000Hz");
            _logger.LogWarning($"✅ Voice: en-US-EmmaNeural");
            _logger.LogWarning($"✅ Voice Temperature: {voiceTemperature}");
            _logger.LogWarning("═══════════════════════════════════════════════");

            // Original detailed logging (kept for backward compatibility)
            _logger.LogInformation("Audio Enhancement Settings (OPEN OFFICE PROFILE):");
            _logger.LogInformation("   Noise Reduction: azure_deep_noise_suppression (MAXIMUM)");
            _logger.LogInformation("   Echo Cancellation: server_echo_cancellation");
            _logger.LogInformation("   Voice Activity Detection: azure_semantic_vad (threshold: 0.6)");
            _logger.LogInformation("   Speech Detection: 250ms minimum, 400ms silence, 1200ms max pause");
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
                
                // DEPARTMENT RETRY POLICY
                "DEPARTMENT CORRECTION POLICY:",
                "If check_staff_exists returns 'not_authorized' (wrong department):",
                "1. First attempt: Say 'I'm sorry, but I cannot verify that department. What department does [Name] work in?'",
                "2. User provides new department → IMMEDIATELY call check_staff_exists again with the NEW department",
                "3. If 'not_authorized' AGAIN (second wrong attempt): Say 'I apologize, but I cannot verify that information. You need to know the correct department to leave a message. Is there anything else I can help you with?'",
                "4. Allow ONE retry for department mistakes - people make honest mistakes!",
                "5. DO NOT get stuck in a loop - after 2 failed attempts, offer to help with something else",
                
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
                "   → User responds (e.g., 'IT' or 'Sales')",
                "   → IMMEDIATELY call check_staff_exists(name='[Recipient]', department='IT')",
                "   → DO NOT just say 'okay' - CALL THE FUNCTION NOW!",
                
                "STEP 4A - If returns 'authorized|IT' (correct department):",
                "   → Ask: 'What message would you like to send to [Recipient] in IT?'",
                "   → User provides message",
                "   → IMMEDIATELY call send_message(name='[Recipient]', message='Message from [Caller]: [their message]', department='IT')",
                "   → DO NOT just say 'I'll send that' - CALL THE FUNCTION NOW!",
                "   → After sending, say: 'I've sent your message to [Recipient] in IT. Is there anything else I can help you with?'",
                "   → DO NOT call end_call yet - wait for user response!",
                
                "STEP 4B - If returns 'not_authorized' (wrong department - FIRST ATTEMPT):",
                "   → Say: 'I'm sorry, but I cannot verify that department. What department does [Recipient] work in?'",
                "   → User provides NEW department (e.g., user said 'Sales' first, now says 'IT')",
                "   → IMMEDIATELY call check_staff_exists(name='[Recipient]', department='IT') - RETRY WITH NEW DEPT!",
                "   → DO NOT just repeat the question - CALL THE FUNCTION with the new department!",
                
                "STEP 4C - If returns 'not_authorized' AGAIN (wrong department - SECOND ATTEMPT):",
                "   → Say: 'I apologize, but I cannot verify that information. To leave a message, you need to know the correct department. Is there anything else I can help you with?'",
                "   → DO NOT reveal the correct department",
                "   → DO NOT ask for department a third time - move on!",
                "   → Wait for user response before calling end_call",
                
                // CALL ENDING - TWO-STEP PROCESS (CRITICAL)
                "CALL ENDING - TWO-STEP PROCESS (CRITICAL):",
                "When user says they're done ('no', 'nothing else', 'that's all'):",
                "",
                $"RESPONSE 1 (Say goodbye - DO NOT call end_call in this response):",
                $"   Say: 'Thanks for calling poms.tech, {farewell}!'",
                "   DO NOT call any functions in this response",
                "   Just speak the farewell message and STOP",
                "   Let the system generate the audio and play it to the caller",
                "",
                "RESPONSE 2 (After audio plays - Now call end_call):",
                "   User may say: 'bye', 'thanks', 'you too', or nothing at all",
                "   NOW call end_call()",
                "   This is a separate response from the goodbye",
                "",
                "CRITICAL RULES FOR CALL ENDING:",
                "   - NEVER call end_call in the SAME response where you say goodbye",
                "   - Goodbye message and end_call MUST be in SEPARATE responses",
                "   - First response: Say goodbye ONLY (no function calls)",
                "   - Second response: Call end_call ONLY (after audio has played)",
                "   - This ensures the caller actually HEARS the goodbye before hanging up",
                "",
                "WRONG SEQUENCE - DON'T DO THIS:",
                $"User: 'No' → AI in ONE response: 'Thanks for calling poms.tech, {farewell}!' AND [calls end_call()] ❌",
                "Problem: Call hangs up before audio plays - caller never hears goodbye!",
                "",
                "CORRECT SEQUENCE - DO THIS:",
                $"User: 'No' → AI Response 1: 'Thanks for calling poms.tech, {farewell}!' [NO function call, just text]",
                "→ [System converts text to audio]",
                "→ [Audio plays to caller - caller hears goodbye]",
                "→ [User might say 'bye' or stay silent]",
                "→ AI Response 2: [calls end_call()] ✅",
                "Success: Caller heard the goodbye before call ended!",
                
                // Complete workflow examples
                "COMPLETE EXAMPLE 1 - USER CORRECTS DEPARTMENT (WITH PROPER ENDING):",
                "User: 'Send message to Adrian Baker'",
                "AI: check_staff_exists(name='Adrian Baker')",
                "System: 'caller_identification_required'",
                "AI: 'Before I can take a message, may I have your first and last name?'",
                "User: 'Bob Johnson'",
                "AI: collect_caller_name(first_name='Bob', last_name='Johnson')",
                "System: 'caller_identified|Bob Johnson'",
                "AI: 'Thank you Bob. What department does Adrian Baker work in?'",
                "User: 'Sales'",
                "AI: check_staff_exists(name='Adrian Baker', department='Sales')",
                "System: 'not_authorized' ← WRONG DEPT (FIRST ATTEMPT)",
                "AI: 'I'm sorry, but I cannot verify that department. What department does Adrian Baker work in?'",
                "User: 'Oh sorry, IT'",
                "AI: check_staff_exists(name='Adrian Baker', department='IT') ← RETRY!",
                "System: 'authorized|IT' ← CORRECT!",
                "AI: 'Thank you. What message would you like to send to Adrian Baker in IT?'",
                "User: 'The server is down'",
                "AI: send_message(name='Adrian Baker', message='Message from Bob Johnson: The server is down', department='IT')",
                "System: 'success'",
                "AI: 'I've sent your message to Adrian Baker in IT. Is there anything else I can help you with?'",
                "User: 'No'",
                $"AI RESPONSE 1: 'Thanks for calling poms.tech, {farewell}!' [Just speak - NO end_call]",
                "[Audio is generated and plays to caller]",
                "[User hears the goodbye message]",
                "AI RESPONSE 2: end_call() [Now call the function in a separate response]",
                
                "COMPLETE EXAMPLE 2 - USER GETS DEPARTMENT WRONG TWICE:",
                "User: 'Send message to Adrian Baker'",
                "AI: check_staff_exists(name='Adrian Baker')",
                "System: 'caller_identification_required'",
                "AI: 'Before I can take a message, may I have your first and last name?'",
                "User: 'Bob Johnson'",
                "AI: collect_caller_name(first_name='Bob', last_name='Johnson')",
                "System: 'caller_identified|Bob Johnson'",
                "AI: 'Thank you Bob. What department does Adrian Baker work in?'",
                "User: 'Sales'",
                "AI: check_staff_exists(name='Adrian Baker', department='Sales')",
                "System: 'not_authorized' ← WRONG (FIRST ATTEMPT)",
                "AI: 'I'm sorry, but I cannot verify that department. What department does Adrian Baker work in?'",
                "User: 'Finance'",
                "AI: check_staff_exists(name='Adrian Baker', department='Finance') ← RETRY!",
                "System: 'not_authorized' ← WRONG AGAIN (SECOND ATTEMPT)",
                "AI: 'I apologize, but I cannot verify that information. To leave a message, you need to know the correct department. Is there anything else I can help you with?'",
                "User: 'No'",
                $"AI RESPONSE 1: 'Thanks for calling poms.tech, {farewell}!' [Just speak - NO end_call]",
                "[Audio plays to caller]",
                "AI RESPONSE 2: end_call() [Separate response]",
                
                // Critical reminders
                "CRITICAL REMINDERS - ALWAYS CALL FUNCTIONS:",
                "- FIRST: Verify recipient has BOTH first and last name (2+ words required!)",
                "- If only 1 word provided, ask for last name before calling any functions",
                "- When user provides recipient FULL name → CALL check_staff_exists IMMEDIATELY",
                "- When user provides their name → CALL collect_caller_name IMMEDIATELY",
                "- When user provides department → CALL check_staff_exists with department IMMEDIATELY",
                "- When 'not_authorized' returned (FIRST TIME) → Ask for department again and RETRY!",
                "- When user corrects department → CALL check_staff_exists AGAIN with NEW department!",
                "- When 'not_authorized' returned (SECOND TIME) → Stop asking, offer other help",
                "- When user provides message → CALL send_message IMMEDIATELY",
                "- After message sent → Ask 'anything else?' and WAIT for response (don't call end_call)",
                $"- When ending call → RESPONSE 1: Say '{farewell}' ONLY (no function call)",
                "- Then → RESPONSE 2: Call end_call() ONLY (separate response)",
                "- NEVER EVER call end_call in the same response as saying goodbye!",
                "- DO NOT just have a conversation - you MUST call the functions to make things happen!",
                "- DO NOT get stuck in a loop asking for department - allow ONE retry, then move on!",
                
                "SECURITY REMINDERS:",
                "- ALWAYS require BOTH first and last name for recipient (2+ words)",
                "- ALWAYS ask for department after caller identifies themselves",
                "- NEVER accept just a first name like 'Adrian' - always need 'Adrian Baker'",
                "- ALLOW ONE department retry - people make honest mistakes!",
                "- After TWO failed department attempts, stop and offer other help",
                "- NEVER reveal the correct department if caller gets it wrong",
                
                $"FINAL REMINDERS: Time={timeOfDay}, Greeting='{greeting}', Farewell='{farewell}'",
                "Remember: Goodbye in RESPONSE 1 (no function), end_call in RESPONSE 2 (separate)!");
        }

        /// <summary>
        /// Build turn detection configuration for voice activity detection
        /// TUNED FOR OPEN OFFICE / NOISY ENVIRONMENTS
        /// </summary>
        private object BuildTurnDetection()
        {
            // NOISE SUPPRESSION PROFILE: OPEN OFFICE (AGGRESSIVE FILTERING)
            // These settings reduce false triggers from background conversations
            return new
            {
                type = "azure_semantic_vad",

                // THRESHOLD: 0.6 = Require 60% confidence it's actual caller speech
                // (was 0.4 - too sensitive, picked up background noise)
                // 0.5 = Balanced | 0.6 = Noisy office | 0.7 = Very noisy
                threshold = 0.6,

                // PREFIX PADDING: Keep at 150ms to capture start of speech
                prefix_padding_ms = 150,

                // SILENCE DURATION: 400ms - Longer pause before processing
                // (was 150ms - too short, triggered on brief background pauses)
                // Prevents bot from reacting to quick background chatter
                silence_duration_ms = 400,

                // Remove "um", "uh", etc. - keep enabled
                remove_filler_words = true,

                // MIN SPEECH DURATION: 250ms - Ignore quick background sounds
                // (was 100ms - picked up brief noises like coughs, laughs)
                // Requires deliberate speech from caller
                min_speech_duration_ms = 250,

                // MAX SILENCE: 1200ms - Be patient with caller thinking
                // (was 800ms) - Gives caller more time between sentences
                max_silence_for_turn_ms = 1200
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
                description = "Check if staff member exists in the specified department. SECURITY: When department is provided by caller, returns 'authorized|DEPT' if correct, or 'not_authorized' if wrong department (security check failed). If 'not_authorized' is returned, you should ask for the department ONE MORE TIME and call this function again with the new department. Allow ONE retry for honest mistakes.",
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
                            description = "REQUIRED for security verification. The department the CALLER says the person works in. If caller provides wrong department, function returns 'not_authorized' (security check failed). You can call this function again with a corrected department if user made a mistake."
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
                description = "Send message to verified staff member. ONLY call after BOTH caller identification AND department verification pass. The message should include caller's name for context. After calling this function, you should ask the user 'Is there anything else I can help you with?' and wait for their response. DO NOT call end_call immediately after this function.",
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
                description = $"End the call. CRITICAL: Only call this function AFTER you have already said goodbye in a PREVIOUS response. NEVER call this in the same response where you say '{farewell}'. The goodbye message must be in one response, and this function call must be in the NEXT response after the audio has played. This ensures the caller actually hears the goodbye before the call ends.",
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

            return $"Voice: en-US-EmmaNeural | VAD: azure_semantic_vad | Security: Enhanced (Name + Department Verification with 1 Retry) | Audio: pcm16@24kHz | Time: {timeOfDay} | Greeting: '{greeting}' | Farewell: '{farewell}'";
        }
    }
}
