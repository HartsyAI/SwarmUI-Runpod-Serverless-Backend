using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Core;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using SwarmUI.Backends;
using SwarmUI.DataHolders;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using Hartsy.Extensions.RunPodServerless.Utils;
using System.Linq;
using System.Collections.Concurrent;

namespace Hartsy.Extensions.RunPodServerless;

public class RunPodServerlessBackend : AbstractT2IBackend
{
    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;

    // Required nested settings class for RegisterBackendType to reflect UI fields.
    public class Settings : AutoConfiguration
    {
        [ConfigComment("RunPod serverless endpoint ID.")]
        public string EndpointId = "";

        [ConfigComment("Use async /run endpoint (recommended)")]
        public bool UseAsync = true;

        [ConfigComment("Max parallel requests")]
        public int MaxConcurrent = 10;

        [ConfigComment("Status poll interval (ms)")]
        public int PollIntervalMs = 2000;

        [ConfigComment("Max job duration (seconds)")]
        public int TimeoutSec = 600;

        [ConfigComment("Optional RunPod API key to use for backend operations (falls back to user key if empty)")]
        [ValueIsSecret]
        public string RunPodApiKey = "";

        [ConfigComment("Attempt to refresh models from worker on backend init (requires a user session)")]
        public bool AutoRefresh = false;
    }

    public Settings Config => (Settings)SettingsRaw;

    /// <summary>Retrieve the RunPod API key from the user's stored keys.</summary>
    private string GetRunPodApiKey(Session session = null, T2IParamInput input = null)
    {
        if (!string.IsNullOrWhiteSpace(Config.RunPodApiKey))
        {
            return Config.RunPodApiKey.Trim();
        }

        Session lookupSession = session ?? input?.SourceSession;
        if (lookupSession?.User == null)
        {
            throw new Exception("RunPod API key not found. Please set a backend API key in settings or ensure the calling user has a RunPod API key configured.");
        }
        string apiKey = lookupSession.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new Exception("RunPod API key not found. Please set your API key in User Settings → API Keys.");
        }
        return apiKey;
    }

    public override async Task Init()
    {
        AddLoadStatus("Starting RunPod serverless backend...");

        if (Config.AutoRefresh)
        {
            try
            {
                AddLoadStatus("Refreshing models from worker...");
                await RefreshModelsFromWorkerAsync();
            }
            catch (Exception ex)
            {
                Logs.Warning($"[RunPodServerless] Auto refresh failed: {ex.Message}");
            }
        }

        Status = BackendStatus.RUNNING;
        AddLoadStatus("RunPod serverless backend ready.");
    }

    /// <summary>Wake a worker, fetch its model list, and mirror remote model cache like SwarmSwarmBackend.</summary>
    public async Task RefreshModelsFromWorkerAsync(Session session = null)
    {
        string apiKey = GetRunPodApiKey(session);
        var client = new RunPodApiClient(apiKey, Config.EndpointId);

        WorkerInfo worker = await WakeupAndWaitForWorkerAsync(client, new T2IParamInput(session));
        Logs.Info($"[RunPodServerless] Refresh using worker {worker.WorkerId} at {worker.PublicUrl}");

        // mirror SwarmSwarmBackend: fetch all subtypes defined in Program.T2IModelSets
        RemoteModels ??= [];
        List<Task> fetches = [];
        foreach (string subtype in Program.T2IModelSets.Keys)
        {
            fetches.Add(Task.Run(async () =>
            {
                try
                {
                    Logs.Info($"[RunPodServerless] Listing models for subtype '{subtype}'");
                    var req = new JObject
                    {
                        ["session_id"] = worker.SessionId,
                        ["path"] = "",
                        ["depth"] = 999,
                        ["subtype"] = subtype,
                        ["allowRemote"] = false,
                        ["sortBy"] = "Name",
                        ["sortReverse"] = false,
                        ["dataImages"] = true
                    };

                    Logs.Info($"[RunPodServerless] POST {worker.PublicUrl}/API/ListModels payload: {req.ToString(Formatting.Indented)}");
                    JObject resp = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListModels", req);
                    Logs.Info($"[RunPodServerless] Response for subtype '{subtype}': {resp.ToString(Formatting.Indented)}");
                    JArray files = resp?["files"] as JArray;
                    if (files is null)
                    {
                        Logs.Warning($"[RunPodServerless] Remote model list empty for subtype {subtype}");
                        return;
                    }

                    Dictionary<string, JObject> remoteModelsParsed = [];
                    foreach (JToken token in files)
                    {
                        if (token is not JObject data)
                        {
                            continue;
                        }
                        string name = data["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            Logs.Info($"[RunPodServerless] Skipping entry without name: {data.ToString(Formatting.Indented)}");
                            continue;
                        }
                        JObject clone = data.DeepClone() as JObject;
                        clone["local"] = false;
                        remoteModelsParsed[name] = clone;
                    }

                    RemoteModels[subtype] = remoteModelsParsed;

                    Models ??= [];
                    Models[subtype] = [.. remoteModelsParsed.Keys];
                    Logs.Info($"[RunPodServerless] Stored {remoteModelsParsed.Count} models for subtype '{subtype}'. Names: {string.Join(", ", remoteModelsParsed.Keys)}");
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[RunPodServerless] Failed to load remote models for subtype {subtype}: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(fetches);

        Logs.Info("[RunPodServerless] Remote model metadata synced from worker");

        await client.ShutdownWorkerAsync();
    }

    public override async Task Shutdown()
    {
        // TODO: Implement: any cleanup and cache persistence if needed.
        Status = BackendStatus.IDLE;
        await Task.CompletedTask;
    }

    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        // Check permissions
        if (!user_input.SourceSession.User.HasPermission(RunPodPermissions.PermUseRunPod))
        {
            throw new Exception("You do not have permission to use RunPod Serverless backends.");
        }

        // Get API key from user session
        string apiKey = GetRunPodApiKey(user_input.SourceSession, user_input);
        
        // Validate configuration
        if (string.IsNullOrEmpty(Config.EndpointId))
        {
            throw new Exception("RunPod Endpoint ID is not configured. Please set it in the backend settings.");
        }

        try
        {
            Logs.Info($"[RunPodServerless] Starting generation on endpoint {Config.EndpointId}");
            
            // Create API client
            var client = new RunPodApiClient(apiKey, Config.EndpointId);
            
            // Step 1: Wake up worker (non-blocking, returns immediately with public URL)
            Logs.Info("[RunPodServerless] Waking up worker...");
            WorkerInfo workerInfo = await WakeupAndWaitForWorkerAsync(client, user_input);
            
            Logs.Info($"[RunPodServerless] Worker ready at {workerInfo.PublicUrl}");
            
            // Step 2: Map parameters to SwarmUI format
            JObject swarmRequest = ParameterMapper.MapToSwarmUIRequest(user_input, workerInfo.SessionId);
            
            // Step 3: Call SwarmUI directly on the worker
            Logs.Info("[RunPodServerless] Calling SwarmUI on worker...");
            JObject swarmResponse = await client.CallSwarmUIAsync(
                workerInfo.PublicUrl,
                "/API/GenerateText2Image",
                swarmRequest
            );
            
            // Step 4: Extract images from response
            List<Image> images = ParameterMapper.ExtractImagesFromResponse(swarmResponse);
            
            Logs.Info($"[RunPodServerless] Generated {images.Count} image(s)");
            
            // Note: Worker will auto-scale down after timeout, no explicit shutdown needed
            // If you want immediate shutdown, call: await client.ShutdownWorkerAsync();
            
            return images.ToArray();
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodServerless] Generation failed: {ex.Message}");
            throw new Exception($"RunPod generation failed: {ex.Message}", ex);
        }
    }

    /// <summary>Wake up worker and wait for it to be ready.</summary>
    private async Task<WorkerInfo> WakeupAndWaitForWorkerAsync(RunPodApiClient client, T2IParamInput user_input)
    {
        // Send wakeup signal (async, doesn't block for keepalive)
        // We use a short keepalive since we'll be making direct calls
        int keepaliveDuration = 60; // 1 minute keepalive
        int keepaliveInterval = 15; // Ping every 15 seconds
        
        // Start wakeup in background (handler will do keepalive pings)
        var wakeupTask = client.WakeupWorkerAsync(keepaliveDuration, keepaliveInterval);
        
        // Wait briefly for the async wakeup to initiate
        await Task.Delay(2000);
        
        // Poll for worker ready status
        int maxWaitSeconds = Config.TimeoutSec;
        int pollIntervalMs = Config.PollIntervalMs;
        int maxAttempts = (maxWaitSeconds * 1000) / pollIntervalMs;
        
        Logs.Info($"[RunPodServerless] Waiting for worker to be ready (max {maxWaitSeconds}s)...");
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                WorkerReadyResponse readyResponse = await client.CheckWorkerReadyAsync();
                
                if (readyResponse.Ready)
                {
                    Logs.Info($"[RunPodServerless] Worker ready after {attempt * pollIntervalMs / 1000}s");
                    return new WorkerInfo
                    {
                        PublicUrl = readyResponse.PublicUrl,
                        SessionId = readyResponse.SessionId,
                        WorkerId = readyResponse.WorkerId
                    };
                }
                
                if (!string.IsNullOrEmpty(readyResponse.Error))
                {
                    Logs.Verbose($"[RunPodServerless] Worker not ready yet: {readyResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Ready check attempt {attempt + 1} failed: {ex.Message}");
            }
            
            await Task.Delay(pollIntervalMs);
        }
        
        throw new TimeoutException($"Worker did not become ready within {maxWaitSeconds} seconds");
    }

    public override IEnumerable<string> SupportedFeatures
    {
        get
        {
            // TODO: Add feature flags as you implement support (e.g., "controlnet", "lora", etc.)
            yield break;
        }
    }
}


