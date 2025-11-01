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

        [ConfigComment("Max worker startup timeout (seconds)")]
        public int StartupTimeoutSec = 120;

        [ConfigComment("Generation timeout (seconds)")]
        public int GenerationTimeoutSec = 300;

        [ConfigComment("Optional RunPod API key to use for backend operations (falls back to user key if empty)")]
        [ValueIsSecret]
        public string RunPodApiKey = "";

        [ConfigComment("Attempt to refresh models from worker on backend init (requires backend API key, not user key)")]
        public bool AutoRefresh = false;
    }

    public Settings Config => (Settings)SettingsRaw;

    /// <summary>Retrieve the RunPod API key from backend config or user's stored keys.</summary>
    public string GetRunPodApiKey(Session session = null, T2IParamInput input = null)
    {
        // Try backend config first
        if (!string.IsNullOrWhiteSpace(Config.RunPodApiKey))
        {
            return Config.RunPodApiKey.Trim();
        }

        // Fall back to user key
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
            // AutoRefresh requires backend API key (not user key) since there's no session during Init
            if (string.IsNullOrWhiteSpace(Config.RunPodApiKey))
            {
                Logs.Warning("[RunPodServerless] AutoRefresh enabled but no backend API key configured. Skipping auto refresh.");
                Logs.Warning("[RunPodServerless] Set RunPodApiKey in backend settings or disable AutoRefresh.");
            }
            else
            {
                try
                {
                    AddLoadStatus("Refreshing models from worker...");
                    await RefreshModelsFromWorkerAsync();
                    Logs.Info($"[RunPodServerless] Auto refresh complete. Loaded {Models?.Values.Sum(list => list.Count) ?? 0} models.");
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[RunPodServerless] Auto refresh failed: {ex.Message}");
                }
            }
        }

        Status = BackendStatus.RUNNING;
        AddLoadStatus("RunPod serverless backend ready.");
    }

    /// <summary>Wake a worker, fetch its model list, and register models in SwarmUI.</summary>
    public async Task RefreshModelsFromWorkerAsync(Session session = null)
    {
        string apiKey = GetRunPodApiKey(session);
        RunPodApiClient client = new RunPodApiClient(apiKey, Config.EndpointId);

        // Wake worker with enough time for cold start + model listing
        int keepaliveDuration = 300; // 5 minutes - enough for cold start (90s) + listing all subtypes
        Logs.Info($"[RunPodServerless] Starting worker for model refresh (keepalive: {keepaliveDuration}s)...");

        WorkerInfo worker = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
        Logs.Info($"[RunPodServerless] Worker ready: {worker.WorkerId} at {worker.PublicUrl}");

        // Fetch models for all registered subtypes
        RemoteModels ??= new ConcurrentDictionary<string, Dictionary<string, JObject>>();
        Models ??= new Dictionary<string, List<string>>();

        List<Task> fetches = new List<Task>();

        foreach (string subtype in Program.T2IModelSets.Keys)
        {
            fetches.Add(Task.Run(async () =>
            {
                try
                {
                    Logs.Info($"[RunPodServerless] Listing models for subtype '{subtype}'");

                    JObject request = new JObject
                    {
                        ["session_id"] = worker.SessionId,
                        ["path"] = "",
                        ["depth"] = 999,
                        ["subtype"] = subtype,
                        ["allowRemote"] = false,
                        ["sortBy"] = "Name",
                        ["sortReverse"] = false
                    };

                    JObject response = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListModels", request);

                    // Parse response - files is an array of strings
                    JArray filesArray = response["files"] as JArray;
                    if (filesArray == null || filesArray.Count == 0)
                    {
                        Logs.Verbose($"[RunPodServerless] No models found for subtype '{subtype}'");
                        return;
                    }

                    List<string> modelNames = new List<string>();
                    Dictionary<string, JObject> modelMetadata = new Dictionary<string, JObject>();

                    foreach (JToken fileToken in filesArray)
                    {
                        string modelName = fileToken.ToString();
                        if (!string.IsNullOrWhiteSpace(modelName))
                        {
                            modelNames.Add(modelName);

                            // Store basic metadata (can be expanded later)
                            modelMetadata[modelName] = new JObject
                            {
                                ["name"] = modelName,
                                ["local"] = false,
                                ["subtype"] = subtype
                            };
                        }
                    }

                    // Store in backend's Models dictionary for SwarmUI to discover
                    Models[subtype] = modelNames;
                    RemoteModels[subtype] = modelMetadata;

                    Logs.Info($"[RunPodServerless] Registered {modelNames.Count} models for subtype '{subtype}'");
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[RunPodServerless] Failed to load models for subtype '{subtype}': {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(fetches);

        int totalModels = Models.Values.Sum(list => list.Count);
        Logs.Info($"[RunPodServerless] Model refresh complete: {totalModels} models across {Models.Count} subtypes");

        // Explicitly shutdown worker to save costs
        await client.ShutdownWorkerAsync();
        Logs.Info("[RunPodServerless] Worker shutdown signal sent");
    }

    public override async Task Shutdown()
    {
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

        // Get API key from user session or backend config
        string apiKey = GetRunPodApiKey(user_input.SourceSession, user_input);

        // Validate configuration
        if (string.IsNullOrEmpty(Config.EndpointId))
        {
            throw new Exception("RunPod Endpoint ID is not configured. Please set it in the backend settings.");
        }

        RunPodApiClient client = new RunPodApiClient(apiKey, Config.EndpointId);
        WorkerInfo workerInfo = null;

        try
        {
            Logs.Info($"[RunPodServerless] Starting generation on endpoint {Config.EndpointId}");

            // Wake up worker with enough time for cold start + ComfyUI load + generation
            int keepaliveDuration = 180; // 3 minutes - cold start (90s) + ComfyUI load (10s) + gen (40s) + buffer
            Logs.Info($"[RunPodServerless] Waking up worker (keepalive: {keepaliveDuration}s)...");

            workerInfo = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
            Logs.Info($"[RunPodServerless] Worker ready: {workerInfo.WorkerId} at {workerInfo.PublicUrl}");

            // Map parameters to SwarmUI format
            JObject swarmRequest = ParameterMapper.MapToSwarmUIRequest(user_input, workerInfo.SessionId);

            // Call SwarmUI directly on the worker
            Logs.Info("[RunPodServerless] Sending generation request to worker...");
            JObject swarmResponse = await client.CallSwarmUIAsync(
                workerInfo.PublicUrl,
                "/API/GenerateText2Image",
                swarmRequest,
                timeoutSeconds: Config.GenerationTimeoutSec
            );

            // Extract images from response
            List<Image> images = ParameterMapper.ExtractImagesFromResponse(swarmResponse);

            if (images.Count == 0)
            {
                throw new Exception("No images returned from generation");
            }

            Logs.Info($"[RunPodServerless] Generated {images.Count} image(s) successfully");

            return images.ToArray();
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodServerless] Generation failed: {ex.Message}");
            throw new Exception($"RunPod generation failed: {ex.Message}", ex);
        }
        finally
        {
            // Always attempt to shutdown worker to save costs
            if (workerInfo != null)
            {
                try
                {
                    Logs.Info("[RunPodServerless] Sending shutdown signal to worker...");
                    await client.ShutdownWorkerAsync();
                    Logs.Info("[RunPodServerless] Worker shutdown signal sent");
                }
                catch (Exception ex)
                {
                    Logs.Verbose($"[RunPodServerless] Shutdown signal failed (worker may have already scaled down): {ex.Message}");
                }
            }
        }
    }

    /// <summary>Wake up worker and poll until ready.</summary>
    public async Task<WorkerInfo> WakeupAndWaitForWorkerAsync(RunPodApiClient client, int keepaliveDuration)
    {
        // Start wakeup with specified keepalive duration
        int keepaliveInterval = 30; // Ping every 30 seconds

        Logs.Verbose($"[RunPodServerless] Initiating wakeup with {keepaliveDuration}s keepalive...");
        Task wakeupTask = client.WakeupWorkerAsync(keepaliveDuration, keepaliveInterval);

        // Give wakeup a moment to start
        await Task.Delay(2000);

        // Poll for worker ready status
        int maxWaitSeconds = Config.StartupTimeoutSec;
        int pollIntervalMs = Config.PollIntervalMs;
        int maxAttempts = (maxWaitSeconds * 1000) / pollIntervalMs;

        Logs.Info($"[RunPodServerless] Polling for worker ready (max {maxWaitSeconds}s, interval {pollIntervalMs}ms)...");

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                WorkerReadyResponse readyResponse = await client.CheckWorkerReadyAsync();

                if (readyResponse.Ready)
                {
                    int elapsedSeconds = (attempt * pollIntervalMs) / 1000;
                    Logs.Info($"[RunPodServerless] Worker ready after {elapsedSeconds}s");

                    return new WorkerInfo
                    {
                        PublicUrl = readyResponse.PublicUrl,
                        SessionId = readyResponse.SessionId,
                        WorkerId = readyResponse.WorkerId,
                        Version = readyResponse.Version
                    };
                }

                if (!string.IsNullOrEmpty(readyResponse.Error))
                {
                    Logs.Verbose($"[RunPodServerless] Worker not ready: {readyResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Ready check failed (attempt {attempt + 1}): {ex.Message}");
            }

            await Task.Delay(pollIntervalMs);
        }

        throw new TimeoutException($"Worker did not become ready within {maxWaitSeconds} seconds");
    }

    public override IEnumerable<string> SupportedFeatures
    {
        get
        {
            // Mark features as supported as you implement them
            yield return "text2image";
            // Future: yield return "lora"; yield return "controlnet"; etc.
        }
    }
}
