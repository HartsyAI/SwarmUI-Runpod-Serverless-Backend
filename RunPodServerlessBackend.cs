using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.RunPodServerless;
using Hartsy.Extensions.RunPodServerless.Models;
using Hartsy.Extensions.RunPodServerless.WebAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Backend for RunPod serverless GPU endpoints with on-demand worker lifecycle management.</summary>
public class RunPodServerlessBackend : AbstractT2IBackend
{
    /// <summary>Cache of remote models by subtype.</summary>
    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;

    public Session Session = null;

    /// <summary>Backend configuration settings.</summary>
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

        [ConfigComment("Attempt to refresh models from worker on backend init")]
        public bool AutoRefresh = false;
    }

    public Settings Config => (Settings)SettingsRaw;

    /// <summary>Retrieve the RunPod API key from backend config or user's stored keys.</summary>
    public string GetRunPodApiKey(Session session = null, T2IParamInput input = null)
    {
        Session sessData = session ?? Session;
        if (sessData?.User == null)
        {
            throw new Exception("RunPod API key not found. Please set a backend API key in settings or ensure the calling user has a RunPod API key configured.");
        }
        string apiKey = sessData.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new Exception("RunPod API key not found. Please set your API key in User Settings → API Keys.");
        }
        return apiKey;
    }

    public override async Task Init()
    {
        AddLoadStatus("Starting RunPod serverless backend...");
        Status = BackendStatus.LOADING;
        if (Config.AutoRefresh)
        {
            Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            string apiKey = GetRunPodApiKey(Session);
            if (apiKey is null)
            {
                Logs.Warning("[RunPodServerless] AutoRefresh enabled but no backend API key configured. Skipping auto refresh.");
                Logs.Warning("[RunPodServerless] Set RunPodApiKey in backend settings or disable AutoRefresh.");
                Status = BackendStatus.LOADING;
                AddLoadStatus("No API key configured.");
            }
            else
            {
                try
                {
                    AddLoadStatus("Refreshing models from worker...");
                    await RefreshModelsFromWorkerAsync(Session);
                    Status = BackendStatus.RUNNING;
                    AddLoadStatus("RunPod serverless backend ready.");
                    Logs.Info($"[RunPodServerless] Auto refresh complete. Loaded {Models?.Values.Sum(list => list.Count) ?? 0} models.");
                }
                catch (Exception ex)
                {
                    Status = BackendStatus.ERRORED;
                    AddLoadStatus("Auto refresh failed: " + ex.Message);
                    Logs.Warning($"[RunPodServerless] Auto refresh failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Wake a worker, fetch its model list, and register models in SwarmUI.</summary>
    public async Task RefreshModelsFromWorkerAsync(Session session = null)
    {
        string apiKey = GetRunPodApiKey(session);
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        int keepaliveDuration = 300;
        Logs.Debug($"[RunPodServerless] Starting worker for model refresh (keepalive: {keepaliveDuration}s)...");
        WorkerInfo worker = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
        Logs.Debug($"[RunPodServerless] Worker ready: {worker.WorkerId} at {worker.PublicUrl}");
        RemoteModels ??= new ConcurrentDictionary<string, Dictionary<string, JObject>>();
        Models ??= new ConcurrentDictionary<string, List<string>>();
        List<Task> fetches = [];
        foreach (string subtype in Program.T2IModelSets.Keys)
        {
            fetches.Add(Task.Run(async () =>
            {
                try
                {
                    Logs.Debug($"[RunPodServerless] Listing models for subtype '{subtype}'");
                    JObject request = new()
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
                    JArray filesArray = response["files"] as JArray;
                    if (filesArray == null || filesArray.Count == 0)
                    {
                        Logs.Verbose($"[RunPodServerless] No models found for subtype '{subtype}'");
                        return;
                    }
                    List<string> modelNames = [];
                    Dictionary<string, JObject> modelMetadata = [];
                    foreach (JToken fileToken in filesArray)
                    {
                        string modelName = fileToken.ToString();
                        if (!string.IsNullOrWhiteSpace(modelName))
                        {
                            modelNames.Add(modelName);
                            modelMetadata[modelName] = new JObject
                            {
                                ["name"] = modelName,
                                ["local"] = false,
                                ["subtype"] = subtype
                            };
                        }
                    }
                    Models[subtype] = modelNames;
                    RemoteModels[subtype] = modelMetadata;
                    Logs.Debug($"[RunPodServerless] Registered {modelNames.Count} models for subtype '{subtype}'");
                }
                catch (Exception ex)
                {
                    Logs.Error($"[RunPodServerless] Failed to load models for subtype '{subtype}': {ex.Message}");
                }
            }));
        }
        await Task.WhenAll(fetches);
        int totalModels = Models.Values.Sum(list => list.Count);
        Logs.Debug($"[RunPodServerless] Model refresh complete: {totalModels} models across {Models.Count} subtypes");
        await client.ShutdownWorkerAsync();
        Logs.Debug("[RunPodServerless] Worker shutdown signal sent");
    }

    public override async Task Shutdown()
    {
        Status = BackendStatus.IDLE;
        await Task.CompletedTask;
    }

    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        if (!user_input.SourceSession.User.HasPermission(RunPodPermissions.PermUseRunPod))
        {
            throw new Exception("You do not have permission to use RunPod Serverless backends.");
        }
        string apiKey = GetRunPodApiKey(user_input.SourceSession, user_input);
        if (string.IsNullOrEmpty(Config.EndpointId))
        {
            throw new Exception("RunPod Endpoint ID is not configured. Please set it in the backend settings.");
        }
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        WorkerInfo workerInfo = null;
        try
        {
            Logs.Debug($"[RunPodServerless] Starting generation on endpoint {Config.EndpointId}");
            int keepaliveDuration = 180;
            Logs.Debug($"[RunPodServerless] Waking up worker (keepalive: {keepaliveDuration}s)...");
            workerInfo = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
            Logs.Debug($"[RunPodServerless] Worker ready: {workerInfo.WorkerId} at {workerInfo.PublicUrl}");
            JObject swarmRequest = BuildGenerationRequest(user_input, workerInfo.SessionId);
            Logs.Debug("[RunPodServerless] Sending generation request to worker...");
            JObject swarmResponse = await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/GenerateText2Image", swarmRequest,
                timeoutSeconds: Config.GenerationTimeoutSec);
            Image[] images = ExtractGeneratedImages(swarmResponse);
            if (images.Length == 0)
            {
                throw new Exception("No images returned from generation");
            }
            Logs.Debug($"[RunPodServerless] Generated {images.Length} image(s) successfully");
            return images;
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodServerless] Generation failed: {ex.Message}");
            throw new Exception($"RunPod generation failed: {ex.Message}", ex);
        }
        finally
        {
            if (workerInfo != null)
            {
                try
                {
                    Logs.Debug("[RunPodServerless] Sending shutdown signal to worker...");
                    await client.ShutdownWorkerAsync();
                    Logs.Debug("[RunPodServerless] Worker shutdown signal sent");
                }
                catch (Exception ex)
                {
                    Logs.Verbose($"[RunPodServerless] Shutdown signal failed (worker may have already scaled down): {ex.Message}");
                }
            }
        }
    }

    /// <summary>Build generation request from T2IParamInput - leverages SwarmUI's parameter structure.</summary>
    public JObject BuildGenerationRequest(T2IParamInput input, string sessionId)
    {
        JObject request = new()
        {
            ["session_id"] = sessionId
        };
        foreach (KeyValuePair<string, object> entry in input.InternalSet.ValuesInput)
        {
            string paramId = entry.Key;
            object value = entry.Value;
            if (value == null) continue;
            if (value is JToken jtoken)
            {
                request[paramId] = jtoken;
            }
            else if (value is string str)
            {
                request[paramId] = str;
            }
            else if (value is int || value is long || value is double || value is bool)
            {
                request[paramId] = JToken.FromObject(value);
            }
            else if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                request[paramId] = JArray.FromObject(enumerable);
            }
            else
            {
                request[paramId] = JToken.FromObject(value);
            }
        }
        return request;
    }

    /// <summary>Extract images from SwarmUI API response using standard format.</summary>
    public Image[] ExtractGeneratedImages(JObject response)
    {
        List<Image> images = [];
        JArray imageArray = response["images"] as JArray;
        if (imageArray == null || imageArray.Count == 0)
        {
            return [];
        }
        foreach (JToken imageToken in imageArray)
        {
            try
            {
                string base64Data = imageToken.ToString();
                byte[] imageBytes = Convert.FromBase64String(base64Data);
                Image img = new(imageBytes, Image.ImageType.IMAGE, "image/png");
                images.Add(img);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[RunPodServerless] Failed to decode image: {ex.Message}");
            }
        }
        return [.. images];
    }

    /// <summary>Wake up worker and poll until ready.</summary>
    public async Task<WorkerInfo> WakeupAndWaitForWorkerAsync(RunPodApiClient client, int keepaliveDuration)
    {
        int keepaliveInterval = 30;
        Logs.Verbose($"[RunPodServerless] Initiating wakeup with {keepaliveDuration}s keepalive...");
        await client.WakeupWorkerAsync(keepaliveDuration, keepaliveInterval);
        await Task.Delay(2000);
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
            yield return "text2image";
        }
    }
}
