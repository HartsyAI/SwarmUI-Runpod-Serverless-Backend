# RunPod Serverless Backend for SwarmUI

A production ready SwarmUI backend extension that enables serverless GPU inference through RunPod's on demand infrastructure. Automatically manages worker lifecycle, provides cost effective scaling, and seamlessly integrates with SwarmUI's generation pipeline.

## Features

- **On-Demand Worker Management** - Automatically wakes and shuts down GPU workers to minimize costs
- **Seamless Integration** - Works like any native SwarmUI backend with full parameter support
- **Flexible API Key Management** - User level api keys
- **Model Discovery** - Automatic remote model enumeration and registration
- **API Endpoints** - SwarmUI endpoints for status monitoring and manual refresh
- **Permission System** - Granular access control through SwarmUI's permission framework
- **Configurable Timeouts** - Adjustable startup, generation, and polling intervals
- **Error Handling** - Gracefully handles worker errors and timeouts

---

## Installation

### Prerequisites
> [!NOTE]
>This extension is designed to work with our custom [RunPod SwarmUI worker](https://github.com/HartsyAI/RunPod-Worker-SwarmUI). Install this first and make sure it is properly running on RunPod before you continue.
>If you prefer tro create your own worker make sure your handler functions are named and behave the same as in our custom worker and that SwarmUI is accessible on the worker.
- SwarmUI installed and running locally or on a server
- RunPod account with serverless endpoint configured
- RunPod API key ([get yours here](https://www.runpod.io/console/user/settings))
- SwarmUI instance deployed on your RunPod serverless endpoint 

### Setup
**Recommended:** SwarmUI Extensions Tab
1. Navigate to **Server → Extensions**
2. Find **RunPod Serverless** in the list
3. Click **Install** and restart SwarmUI when prompted

**Manual Installation:**

1. **Clone This Repo** to your SwarmUI installation:
   ```
   SwarmUI/src/Extensions/
   ```
2. **Restart SwarmUI** 
   - Stop Swarm and use the update or Dev scripts to rebuild with the new extension 
3. **Verify installation** in SwarmUI:
   - Navigate to **Server → Extensions**
   - Confirm "RunPod Serverless" appears in the list

---

## Configuration

### Backend Configuration

1. Navigate to **Server → Backends**
2. Toggle the **Show Advanced** option
3. Select **RunPod Serverless** from the type dropdown
4. Configure settings:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| **EndpointId** | string | (required) | Your RunPod serverless endpoint ID |
| **UseAsync** | bool | `true` | Use async /run endpoint (recommended) |
| **MaxConcurrent** | int | `10` | Maximum parallel generation requests |
| **PollIntervalMs** | int | `2000` | Worker ready status polling interval (milliseconds) |
| **StartupTimeoutSec** | int | `120` | Maximum time to wait for worker startup (seconds) |
| **GenerationTimeoutSec** | int | `300` | Maximum time for generation completion (seconds) |
| **RunPodApiKey** | string | (optional) | Backend-level API key (falls back to user key if empty) |
| **AutoRefresh** | bool | `false` | Refresh models on backend initialization (requires backend API key) |

### API Key Configuration

**Option 1: Backend-Level Key** (Recommended for shared backends)
- Set `RunPodApiKey` in backend settings
- All users share this key
- Required for `AutoRefresh` feature

**Option 2: User-Level Key** (Recommended for multi-user installations)
- Navigate to **User Settings → API Keys**
- Add RunPod API key
- Each user uses their own key

The backend automatically falls back from backend-level to user-level keys.

---

## Usage

### Basic Generation

Once configured, the RunPod backend works like any SwarmUI backend:

1. Select a model from the model dropdown
2. Configure generation parameters
3. Click **Generate**

The backend automatically:
- Wakes the RunPod worker
- Waits for ready state
- Sends generation request
- Returns generated images
- Shuts down the worker

### Model Management

**Automatic Discovery** (if `AutoRefresh` enabled):
- Models are discovered on backend initialization
- Requires backend-level API key

**Manual Refresh**:
```bash
curl -X POST http://localhost:7801/API/RunPod/RefreshModels
```

Or use the SwarmUI API interface.

### Status Monitoring

Check backend status:
```bash
curl -X GET http://localhost:7801/API/RunPod/GetStatus
```

---

## API Reference

### REST Endpoints

#### `POST /API/RunPod/RefreshModels`

Manually refreshes model list from all running RunPod backends.

**Request:** None

**Response:**
```json
{
  "success": true,
  "refreshed": 1,
  "failed": 0,
  "message": "Refreshed 1 RunPod backend(s), 0 failed."
}
```

**Permissions:** Requires `use_runpod_serverless` permission

---

#### `GET /API/RunPod/GetStatus`

Returns status information for all RunPod backends.

**Request:** None

**Response:**
```json
{
  "success": true,
  "backends": [
    {
      "id": 1,
      "title": "RunPod Serverless",
      "status": "RUNNING",
      "endpoint_id": "abc123xyz",
      "model_count": 42,
      "auto_refresh": false,
      "max_concurrent": 10
    }
  ],
  "total_backends": 1
}
```

**Permissions:** Requires `use_runpod_serverless` permission

---

## Architecture

### Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                         SwarmUI                              │
│  ┌──────────────────────────────────────────────────────┐  │
│  │         RunPodServerlessBackend                      │  │
│  │  • Parameter serialization                           │  │
│  │  • Image extraction                                  │  │
│  │  • Model management                                  │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                            │
│  ┌──────────────▼───────────────────────────────────────┐  │
│  │         RunPodApiClient                              │  │
│  │  • Worker lifecycle (wake/poll/shutdown)            │  │
│  │  • HTTP communication                                │  │
│  │  • RunPod API integration                           │  │
│  └──────────────┬───────────────────────────────────────┘  │
└─────────────────┼───────────────────────────────────────────┘
                  │
                  │ HTTPS
                  ▼
         ┌─────────────────────┐
         │   RunPod API        │
         │  • /run endpoint    │
         │  • /runsync         │
         │  • /status/{id}     │
         └──────────┬──────────┘
                    │
                    ▼
         ┌─────────────────────┐
         │  RunPod Worker      │
         │  • SwarmUI instance │
         │  • Model storage    │
         │  • GPU inference    │
         └─────────────────────┘
```

### Generation Flow

```
1. User submits generation request
   ↓
2. Backend wakes RunPod worker
   • POST to RunPod /run endpoint
   • Keepalive signal started
   ↓
3. Poll for worker ready state
   • Check worker status every PollIntervalMs
   • Timeout after StartupTimeoutSec
   ↓
4. Worker ready - get public URL and session ID
   ↓
5. Forward generation request
   • Serialize T2IParamInput to JSON
   • POST to worker SwarmUI /API/GenerateText2Image
   ↓
6. Receive and process response
   • Extract base64 images from response
   • Convert to Image objects
   ↓
7. Shutdown worker
   • Send shutdown signal to RunPod
   • Worker scales to zero
   ↓
8. Return images to user
```

### Worker Lifecycle

The backend manages three distinct phases:

**Wakeup Phase:**
- Sends wake signal to RunPod endpoint
- Initiates keepalive heartbeat
- Non-blocking operation

**Ready Phase:**
- Polls worker status endpoint
- Waits for SwarmUI initialization
- Retrieves worker URL and session ID
- Configurable timeout and interval

**Shutdown Phase:**
- Sends graceful shutdown signal
- Worker scales to zero
- Cost savings immediately

---

## Troubleshooting

### Common Issues

#### No RunPod API key found

**Symptoms:**
```
Exception: RunPod API key not found. Please set a backend API key...
```

**Solution:**
- Set backend API key in backend settings, OR
- Configure user API key in User Settings → API Keys → RunPod

---

#### Worker did not become ready within timeout

**Symptoms:**
```
TimeoutException: Worker did not become ready within 120 seconds
```

**Solutions:**
1. Increase `StartupTimeoutSec` in backend settings
2. Check RunPod dashboard for worker errors
3. Verify endpoint ID is correct
4. Check RunPod account balance and worker availability

---

#### No images returned from generation

**Symptoms:**
```
Exception: No images returned from generation
```

**Solutions:**
1. Check worker logs in RunPod dashboard
2. Verify model exists on remote worker
3. Ensure generation parameters are valid
4. Increase `GenerationTimeoutSec` if generation is slow

---

#### Worker fails to start consistently

**Symptoms:**
- Multiple timeout errors
- Workers stuck in starting state

**Solutions:**
1. Check RunPod service status
2. Verify endpoint configuration in RunPod dashboard
3. Review worker Docker image configuration
4. Check available GPU capacity in RunPod region

---

### Debug Logging

Enable verbose logging to diagnose issues:

```csharp
// Logs appear in SwarmUI console output
// Look for tags:
[RunPodServerless] - Backend operations
[RunPodApiClient] - API communication
```

Common log patterns:

**Successful generation:**
```
[RunPodServerless] Starting generation on endpoint abc123
[RunPodServerless] Waking up worker (keepalive: 180s)...
[RunPodServerless] Worker ready after 45s
[RunPodServerless] Sending generation request to worker...
[RunPodServerless] Generated 1 image(s) successfully
[RunPodServerless] Worker shutdown signal sent
```

**Connection issues:**
```
[RunPodApiClient] RunPod API call failed (503): Service Unavailable
```

**Parameter issues:**
```
[RunPodServerless] Failed to decode image: Invalid base64 string
```

---

## Development

### Prerequisites

- .NET 8 SDK
- SwarmUI development environment
- Understanding of SwarmUI's backend architecture

### Building

The extension compiles as part of SwarmUI's build process:

```bash
cd SwarmUI
dotnet build
```

### Testing

1. **Unit Testing** - Test individual components:
   ```csharp
   // Test parameter serialization
   var input = new T2IParamInput();
   var request = backend.BuildGenerationRequest(input, "session-id");
   Assert.NotNull(request["session_id"]);
   ```

2. **Integration Testing** - Test full generation flow:
   - Configure backend with test endpoint
   - Generate test image
   - Verify logs show proper lifecycle

3. **Load Testing** - Test concurrent requests:
   - Configure `MaxConcurrent` setting
   - Submit multiple generations simultaneously
   - Monitor worker scaling behavior

### Code Style

This extension follows SwarmUI's coding standards:

- **No `var` keyword** - Use explicit types
- **Primary constructors** - When appropriate
- **Public fields** - Prefer over private with properties
- **`Logs` class** - For all logging operations
- **Modular design** - Reusable, site-wide components

### Extension Structure

```csharp
// Main backend class
public class RunPodServerlessBackend : AbstractT2IBackend
{
    // Configuration
    public class Settings : AutoConfiguration { }
    
    // Core methods
    public override Task Init() { }
    public override Task<Image[]> Generate(T2IParamInput input) { }
    public override Task Shutdown() { }
}

// Extension registration
public class RunPodServerlessExtension : Extension
{
    public override void OnInit() { }
}

// Web API
public static class RunPodWebAPI
{
    public static void Register() { }
}
```

---

## Performance Considerations

### Cost Optimization

**Worker Lifecycle:**
- Workers start only when needed
- Automatic shutdown after generation
- Configurable keepalive for batch operations

**Keepalive Strategy:**
```
Single generation: 180s keepalive (cold start + generation + buffer)
Batch operations: Adjust keepalive to cover all generations
Model refresh: 300s keepalive (enough for model listing across all types)
```

### Latency Management

**Cold Start:**
- Typical: 60-90 seconds
- Includes: Container start, SwarmUI init, model discovery

**Warm Generation:**
- If worker still running from previous generation
- Near-instant start (no cold start penalty)

**Optimization:**
- Set appropriate `StartupTimeoutSec` for your endpoint
- Use `PollIntervalMs` to balance responsiveness vs. API calls

### Concurrent Requests

The backend supports concurrent generations through `MaxConcurrent` setting:

```
MaxConcurrent: 1  - Serial processing (cheapest)
MaxConcurrent: 5  - Moderate parallelism
MaxConcurrent: 10 - High throughput (default)
```

Note: Each concurrent request wakes a separate worker instance.

---

## Security

### API Key Management

**Backend Keys:**
- Stored in backend configuration
- Encrypted at rest by SwarmUI
- Visible only to administrators

**User Keys:**
- Stored in user profile
- Isolated per user
- Not visible to other users

### Permissions

The extension integrates with SwarmUI's permission system:

```
use_runpod_serverless - Required to use RunPod backends
```

Assign to user roles:
- **Admin** - Full access (default)
- **PowerUser** - Can use RunPod backends (default)
- **User** - No access (default)

### Network Security

**Outbound Connections:**
- `api.runpod.ai` - RunPod API
- `[worker-id].runpod.io` - Individual worker instances

**Inbound Connections:**
- None required (RunPod workers do not connect back)

---

## Compatibility

### SwarmUI Versions

- **Tested:** SwarmUI v0.9.7+
- **Minimum:** SwarmUI v0.9.0 (requires AbstractT2IBackend)

### RunPod Requirements

- Serverless endpoint with SwarmUI Docker image
- Workers must expose SwarmUI API on standard port
- Minimum worker configuration:
  - 1 vCPU
  - 4GB RAM
  - GPU with sufficient VRAM for target models

### Model Support

Supports all model types available in remote SwarmUI instance:
- Stable Diffusion (1.5, 2.x, XL)
- Flux
- LoRA models
- ControlNet models
- All other SwarmUI-compatible architectures

---

## License

This extension follows SwarmUI's MIT license. See SwarmUI repository for full license text.

---

## Support

### Resources

- **SwarmUI Documentation:** [https://github.com/mcmonkeyprojects/SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI)
- **RunPod Documentation:** [https://docs.runpod.io/](https://docs.runpod.io/)
- **SwarmUI Discord:** [https://discord.gg/q2y38cqjNw](https://discord.gg/q2y38cqjNw)

### Reporting Issues

When reporting issues, include:
1. SwarmUI version
2. Backend configuration (redact API keys)
3. Relevant log output
4. RunPod endpoint details (worker configuration)
5. Steps to reproduce

---

## Acknowledgments

- **SwarmUI** - Backend framework and infrastructure
- **RunPod** - Serverless GPU platform
- **SwarmSwarmBackend** - Pattern reference for remote SwarmUI integration

---

**Version:** 2.0  
**Status:** Production Ready  
**Last Updated:** 2025-11-02