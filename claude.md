# Claude AI Assistant Guide for Azure Voice AI Project

This document provides context for AI assistants (like Claude) working on this codebase. It contains project overview, architecture, key configurations, common issues, and development workflows.

---

## 📋 Project Overview

**Azure Voice AI** is a voice-first virtual assistant built with:
- **Azure Communication Services (ACS)** - PSTN calling and media streaming
- **Azure AI Voice Live** - Real-time voice interaction via WebSocket
- **Azure OpenAI** - GPT-4o-mini-realtime-preview model for conversational AI
- **.NET 8** - C# ASP.NET Core Web Application

**Primary Use Case**: After-hours messaging service for poms.tech that:
1. Answers incoming phone calls
2. Identifies caller and message recipient
3. Validates recipient exists and caller knows their department (security)
4. Sends email messages via Microsoft Graph API
5. Manages call flow with sophisticated voice activity detection

---

## 🏗️ Architecture Overview

### Call Flow
```
PSTN Caller → ACS Phone Number → Event Grid → Web App (/api/incomingCall)
    ↓
Web App answers call + starts media streaming
    ↓
Audio streams via WebSocket (bidirectional)
    ↓
Azure AI Voice Live (VAD + STT + LLM + TTS)
    ↓
Web App receives AI responses and streams back to caller
```

### Key Components

#### Entry Points
- `Program.cs:49` - `/api/incomingCall` - Event Grid webhook for incoming calls
- `Program.cs:141` - `/api/callbacks/{contextId}` - ACS callback handler

#### Core Services

**AzureVoiceLiveService.cs** - Manages WebSocket connection to Azure AI Voice Live
- Handles bidirectional audio streaming
- Manages session lifecycle
- Processes real-time events (audio.delta, response.done, etc.)

**AcsMediaStreamingHandler.cs** - Handles ACS media streaming
- Receives audio from caller via WebSocket
- Sends AI-generated audio back to caller
- Manages media stream lifecycle

**SessionConfigBuilder.cs** - Builds Voice Live session configuration
- **CRITICAL**: Contains VAD (Voice Activity Detection) settings
- Defines AI instructions and behavior
- Configures function calling tools

**Services/Voice/MessageProcessor.cs** - Processes Voice Live events
- Handles transcription, audio deltas, function calls
- Routes events to appropriate handlers

**Services/Voice/CallFlowManager.cs** - Manages conversation state
- Tracks caller identification
- Handles department verification
- Manages retry logic

**StaffLookupService.cs** - Staff directory integration
- Azure Table Storage for staff data
- Fuzzy matching for name variations
- Department-based security validation

**EmailService.cs** - Microsoft Graph API integration
- Sends emails to staff members
- Includes caller information and message content

---

## ⚙️ Critical Configuration: VAD Settings

**Location**: `Services/Voice/SessionConfigBuilder.cs:291`

### Current Profile: NOISY_ENVIRONMENT

```csharp
threshold = 0.65               // 65% confidence required (high threshold)
prefix_padding_ms = 180        // Captures 180ms before speech starts
silence_duration_ms = 500      // Wait 500ms of silence before processing
min_speech_duration_ms = 400   // Minimum 400ms of speech required
max_silence_for_turn_ms = 1500 // Allow 1.5s pause between sentences
remove_filler_words = true     // Filter "um", "uh", etc.
```

### VAD Profile History

**Recent Changes** (see git log):
1. **QUIET_OFFICE** (threshold: 0.5) - Responsive but picked up TV background noise
2. **NOISY_ENVIRONMENT** (threshold: 0.65) - Current setting, filters background noise

### When to Adjust VAD Settings

| Issue | Adjustment | Setting |
|-------|-----------|---------|
| Background noise triggers false speech | Increase threshold | 0.65 → 0.7 |
| System cuts callers off mid-sentence | Increase silence duration | 500ms → 600ms |
| System feels sluggish/unresponsive | Decrease silence duration | 500ms → 300ms |
| TV/radio triggering system | Increase min speech + threshold | 400ms → 500ms, 0.65 → 0.7 |
| Missing soft-spoken callers | Decrease threshold | 0.65 → 0.55 |
| Too many "um" sounds getting through | Already enabled: `remove_filler_words = true` |

---

## 🔧 Important Configuration Files

### appsettings.json

**Required Settings**:
```json
{
  "AppBaseUrl": "https://your-tunnel-or-webapp.com",
  "AcsConnectionString": "endpoint=https://....communication.azure.com/;accesskey=...",
  "AzureVoiceLiveApiKey": "...",
  "AzureVoiceLiveEndpoint": "https://....cognitiveservices.azure.com/",
  "VoiceLiveModel": "gpt-4o-mini-realtime-preview",

  "GraphTenantId": "...",
  "GraphClientId": "...",
  "GraphClientSecret": "...",
  "GraphSenderUPN": "terry@poms.tech",

  "StorageUri": "https://....table.core.windows.net",
  "StorageAccountName": "...",
  "StorageAccountKey": "...",
  "TableName": "StaffDirectory"
}
```

---

## 🐛 Common Issues and Solutions

### Issue: Random call dropouts / timeouts

**Recent Fix** (commit: 1e6f98c):
- Increased WebSocket timeout from 30s to 120s
- Location: `AzureVoiceLiveService.cs`

### Issue: Background TV/radio triggering system

**Recent Fix** (commit: f4764f4):
- Updated VAD profile to NOISY_ENVIRONMENT (threshold 0.65)
- Location: `Services/Voice/SessionConfigBuilder.cs:291`

### Issue: Session lookup failures

**Recent Fix** (commit: c2a1c89):
- URL-encode callerId to prevent session lookup failures
- Location: Call session management

### Issue: Caller gets cut off mid-sentence

**Solution**: Adjust VAD settings
```csharp
silence_duration_ms = 600  // Increase from 500ms
max_silence_for_turn_ms = 1800  // Increase from 1500ms
```

### Issue: System picks up background conversations

**Solution**: Increase threshold and min speech duration
```csharp
threshold = 0.7  // Increase from 0.65
min_speech_duration_ms = 500  // Increase from 400ms
```

---

## 📁 Project Structure

```
Azure-Voice-AI/
├── Program.cs                    # Main app entry, API endpoints
├── AzureVoiceLiveService.cs      # WebSocket connection to Voice Live
├── AcsMediaStreamingHandler.cs   # ACS media streaming
├── Models/
│   └── SessionConfig.cs          # Configuration models
├── Services/
│   ├── Voice/
│   │   ├── SessionConfigBuilder.cs   # ⚠️ VAD settings here
│   │   ├── MessageProcessor.cs       # Event processing
│   │   └── CallFlowManager.cs        # Conversation state
│   ├── Staff/
│   │   ├── StaffLookupService.cs     # Staff directory
│   │   ├── FuzzyMatchingService.cs   # Name matching
│   │   └── Matching/                 # String similarity algorithms
│   ├── EmailService.cs               # Graph API email
│   ├── CallManagementService.cs      # ACS call control
│   └── Interfaces/                   # Service contracts
└── Helpers/
    └── TimeOfDayHelper.cs        # Greeting/farewell generation
```

---

## 🔐 Security Features

### Two-Factor Authentication
1. **Caller Identification**: Must provide first + last name
2. **Department Verification**: Must know recipient's correct department

### Department Retry Policy
- Allows **ONE** retry if caller provides wrong department
- After 2 failed attempts, politely denies message delivery
- Prevents unauthorized messages from random callers

---

## 🚀 Development Workflow

### Local Development

```bash
# 1. Clone and setup
git clone https://github.com/p0mmy1978/Azure-Voice-AI.git
cd Azure-Voice-AI

# 2. Configure appsettings.json (see example above)

# 3. Setup dev tunnel
devtunnel user login
devtunnel create --allow-anonymous
devtunnel port create -p 49412
devtunnel host

# 4. Build and run
dotnet build
dotnet run
```

### Making Changes to VAD Settings

```bash
# 1. Create feature branch
git checkout -b feature/adjust-vad-settings

# 2. Edit Services/Voice/SessionConfigBuilder.cs:291
# Modify threshold, silence_duration_ms, etc.

# 3. Update logging messages to reflect new profile
# Edit lines 50-84 in SessionConfigBuilder.cs

# 4. Test with real calls

# 5. Commit and push
git add Services/Voice/SessionConfigBuilder.cs
git commit -m "Adjust VAD settings for [reason]"
git push origin feature/adjust-vad-settings
```

### Deployment

**Azure Web App**:
1. Push changes to main branch
2. Azure Web App auto-deploys from GitHub
3. Configure App Settings with environment variables
4. Update Event Grid callback if needed

---

## 🧪 Testing Checklist

### After VAD Changes
- [ ] Test with TV background noise - should NOT trigger
- [ ] Test with normal conversational speech - should work
- [ ] Test with quiet/soft-spoken callers - adjust if needed
- [ ] Test with long pauses (thinking) - shouldn't cut off
- [ ] Test with fast-paced conversation - shouldn't lag

### After Code Changes
- [ ] Build succeeds: `dotnet build`
- [ ] No compilation errors
- [ ] Test call flow end-to-end
- [ ] Check logs for errors/warnings
- [ ] Verify email delivery works
- [ ] Test department verification logic

---

## 📊 Key Metrics to Monitor

### Application Logs
- **Connection stability**: WebSocket disconnections
- **VAD triggers**: False positives from background noise
- **Function calls**: Proper execution of check_staff_exists, send_message, etc.
- **Email delivery**: Success/failure rates
- **Session timeouts**: Call duration and timeout events

### Performance
- **WebSocket latency**: Should be < 500ms
- **Audio streaming**: No dropouts or stuttering
- **Response time**: AI should respond within 1-2 seconds

---

## 🔍 Debugging Tips

### Enable Verbose Logging

Check `Program.cs` for logging configuration. Look for:
```csharp
_logger.LogDebug(...)  // Detailed execution flow
_logger.LogInformation(...)  // Standard events
_logger.LogWarning(...)  // VAD configuration, important events
_logger.LogError(...)  // Failures
```

### Common Log Searches

```bash
# Find VAD configuration logs
grep "VAD Threshold" logs.txt

# Find WebSocket issues
grep "WebSocket" logs.txt | grep -i "error\|disconnect\|timeout"

# Find function call issues
grep "Processing function call" logs.txt

# Find email delivery status
grep "Email sent" logs.txt
```

### WebSocket Connection Issues

Check `AzureVoiceLiveService.cs`:
- Timeout setting (currently 120s)
- Connection retry logic
- Proper session initialization

---

## 📚 Additional Resources

- [Azure Communication Services Docs](https://learn.microsoft.com/azure/communication-services/)
- [Azure AI Voice Live](https://learn.microsoft.com/azure/ai-services/speech-service/)
- [OpenAI Realtime API](https://platform.openai.com/docs/guides/realtime)
- [Microsoft Graph API](https://learn.microsoft.com/graph/)

---

## 🎯 Quick Reference: Common Tasks

### Task: Increase VAD sensitivity (pick up quieter speech)
**File**: `Services/Voice/SessionConfigBuilder.cs:303`
```csharp
threshold = 0.55  // Decrease from 0.65
```

### Task: Reduce VAD sensitivity (filter more noise)
**File**: `Services/Voice/SessionConfigBuilder.cs:303`
```csharp
threshold = 0.75  // Increase from 0.65
min_speech_duration_ms = 500  // Increase from 400
```

### Task: Make system more patient (don't cut off callers)
**File**: `Services/Voice/SessionConfigBuilder.cs:311,322`
```csharp
silence_duration_ms = 700  // Increase from 500
max_silence_for_turn_ms = 2000  // Increase from 1500
```

### Task: Make system more responsive (faster replies)
**File**: `Services/Voice/SessionConfigBuilder.cs:311,322`
```csharp
silence_duration_ms = 350  // Decrease from 500
max_silence_for_turn_ms = 1000  // Decrease from 1500
```

---

## 🆘 Emergency Rollback

If a change breaks the system:

```bash
# 1. Check recent commits
git log --oneline -5

# 2. Rollback to last known good commit
git revert <commit-hash>

# OR hard reset (use with caution)
git reset --hard <good-commit-hash>
git push --force origin main

# 3. Rebuild and redeploy
dotnet build
# Restart service
```

---

## 📝 Recent Important Changes

See `git log` for full history. Key recent fixes:

1. **f4764f4** - NOISY_ENVIRONMENT VAD profile (threshold 0.65) to prevent TV false triggers
2. **3cb103e** - Switch from RADIO_TV to QUIET_OFFICE profile
3. **1e6f98c** - WebSocket timeout increased to 120s (prevents dropouts)
4. **c2a1c89** - URL-encode callerId for session lookup
5. **57a8b6f** - Session timeout race condition fix

---

## ✅ Checklist for New AI Assistant Sessions

When starting a new task:
- [ ] Read this claude.md file completely
- [ ] Understand current VAD settings and why they're configured this way
- [ ] Check recent git commits for context
- [ ] Review relevant service files in Services/ directory
- [ ] Test changes with real phone calls when possible
- [ ] Update documentation if making significant changes
- [ ] Follow git branch naming: `claude/task-description-<session-id>`

---

**Last Updated**: 2025-11-01
**Current VAD Profile**: NOISY_ENVIRONMENT (threshold: 0.65)
**Current Branch**: claude/review-vad-settings-011CUgnRnr3G55eYquFcfBGe
