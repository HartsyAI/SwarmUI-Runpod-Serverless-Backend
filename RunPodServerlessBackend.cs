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
using System.Linq;

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
        public int StartupTimeoutSec = 800;

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
        CanLoadModels = false;
        Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
        try
        {
            string apiKey = GetRunPodApiKey(Session);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new Exception("No RunPod API key configured. Add a RunPod API key in User Settings → API Keys.");
            }
            AddLoadStatus("Refreshing models from worker (with retries)...");
            await RefreshModelsFromWorkerAsync(Session);
            Status = BackendStatus.RUNNING;
            AddLoadStatus("RunPod serverless backend ready.");
            Logs.Info($"[RunPodServerless] Model refresh complete. Loaded {Models?.Values.Sum(list => list.Count) ?? 0} models.");
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus("Model refresh failed: " + ex.Message);
            Logs.Error($"[RunPodServerless] Init failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Poll the worker's Swarm until all backends have finished loading.</summary>
    public async Task WaitForWorkerBackendsLoadedAsync(RunPodApiClient client, WorkerInfo worker, int timeoutSec)
    {
        int pollMs = Math.Clamp(Config.PollIntervalMs, 500, 5000);
        int attempts = Math.Max(1, (timeoutSec * 1000) / pollMs);
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                JObject list = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListBackends", new JObject
                {
                    ["session_id"] = worker.SessionId,
                    ["nonreal"] = true,
                    ["full_data"] = true
                }, timeoutSeconds: 30);
                var statuses = list.Properties().Select(p => p.Value).OfType<JObject>().Select(b => (id: b["id"]?.ToString(), status: b["status"]?.ToString())).ToList();
                bool anyLoading = statuses.Any(x => string.Equals(x.status, "loading", StringComparison.OrdinalIgnoreCase));
                Logs.Debug($"[RunPodServerless] Worker backend statuses: {string.Join(", ", statuses.Select(s => $"{s.id}:{s.status}"))}");
                if (!anyLoading)
                {
                    Logs.Debug("[RunPodServerless] Worker backends are loaded.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Error while checking worker backend status: {ex.Message}");
            }
            await Task.Delay(pollMs);
        }
        Logs.Verbose("[RunPodServerless] Timed out waiting for worker backends to finish loading; proceeding anyway.");
    }

    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Session sess = input?.SourceSession ?? Session ?? Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            string apiKey = GetRunPodApiKey(sess, input);
            RunPodApiClient client = new(apiKey, Config.EndpointId);
            int keepaliveDuration = Math.Max(120, Config.StartupTimeoutSec);
            Logs.Debug($"[RunPodServerless] LoadModel requested: {(model?.Name ?? "<null>")}. Waking worker (keepalive {keepaliveDuration}s)...");
            WorkerInfo worker = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
            Logs.Debug($"[RunPodServerless] Worker ready for LoadModel: {worker.WorkerId} at {worker.PublicUrl}");
            await WaitForWorkerBackendsLoadedAsync(client, worker, Config.StartupTimeoutSec);
            string desiredModel = null;
            if (model is not null)
            {
                desiredModel = model.Name;
            }
            else if (input is not null)
            {
                object m = input.Get(T2IParamTypes.Model);
                if (m is T2IModel tm) { desiredModel = tm.Name; }
                else if (m is string ms) { desiredModel = ms; }
            }
            if (string.IsNullOrWhiteSpace(desiredModel))
            {
                Logs.Warning("[RunPodServerless] LoadModel called without a valid model name.");
                return false;
            }
            Logs.Debug($"[RunPodServerless] Selecting model on worker: {desiredModel}");
            JObject req = new()
            {
                ["session_id"] = worker.SessionId,
                ["model"] = desiredModel
            };
            JObject resp = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/SelectModel", req, timeoutSeconds: 120);
            bool success = resp.TryGetValue("success", out JToken s) && s.Value<bool>();
            if (!success)
            {
                Logs.Warning($"[RunPodServerless] SelectModel failed for '{desiredModel}', response: {resp.ToString(Newtonsoft.Json.Formatting.None)}");
                // Fallback: toggle .safetensors suffix
                string alt = desiredModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                    ? desiredModel[..^".safetensors".Length]
                    : desiredModel + ".safetensors";
                if (!string.Equals(alt, desiredModel, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Debug($"[RunPodServerless] Retrying SelectModel with alt name: {alt}");
                    req["model"] = alt;
                    resp = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/SelectModel", req, timeoutSeconds: 120);
                    success = resp.TryGetValue("success", out s) && s.Value<bool>();
                    if (!success)
                    {
                        Logs.Warning($"[RunPodServerless] Alt SelectModel also failed for '{alt}', response: {resp.ToString(Newtonsoft.Json.Formatting.None)}");
                    }
                    else
                    {
                        desiredModel = alt;
                    }
                }
            }
            if (success)
            {
                CurrentModelName = desiredModel;
                Logs.Debug($"[RunPodServerless] Model selected on worker successfully: {desiredModel}");
            }
            return success;
        }
        catch (Exception ex)
        {
            Logs.Verbose($"[RunPodServerless] LoadModel failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Wake a worker, robustly fetch its model list with retries, and register models in SwarmUI.</summary>
    public async Task RefreshModelsFromWorkerAsync(Session session = null)
    {
        string apiKey = GetRunPodApiKey(session);
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        // Keep the worker alive for at least our retry window.
        int retryIntervalMs = 10_000; // 10 seconds between attempts
        int maxWaitSec = Math.Max(Config.StartupTimeoutSec, 120);
        int keepaliveDuration = maxWaitSec + 60; // buffer above max wait
        Logs.Debug($"[RunPodServerless] Starting worker for model refresh (keepalive: {keepaliveDuration}s)...");
        WorkerInfo worker = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
        Logs.Debug($"[RunPodServerless] Worker ready: {worker.WorkerId} at {worker.PublicUrl}");

        DateTime start = DateTime.UtcNow;
        Exception lastError = null;
        try
        {
            while (true)
            {
                try
                {
                    // temp holders to only commit once we have a non-empty set
                    var tempModels = new ConcurrentDictionary<string, List<string>>();
                    var tempRemoteModels = new ConcurrentDictionary<string, Dictionary<string, JObject>>();

                    List<Task> fetches = [];
                    foreach (string subtype in Program.T2IModelSets.Keys)
                    {
                        string subtypeLocal = subtype;
                        fetches.Add(Task.Run(async () =>
                        {
                            try
                            {
                                Logs.Debug($"[RunPodServerless] Listing models for subtype '{subtypeLocal}'");
                                JObject request = new()
                                {
                                    ["session_id"] = worker.SessionId,
                                    ["path"] = "",
                                    ["depth"] = 999,
                                    ["subtype"] = subtypeLocal,
                                    ["allowRemote"] = false,
                                    ["sortBy"] = "Name",
                                    ["sortReverse"] = false,
                                    ["dataImages"] = true
                                };
                                JObject response = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListModels", request);
                                JArray filesArray = response["files"] as JArray;
                                if (filesArray == null || filesArray.Count == 0)
                                {
                                    Logs.Verbose($"[RunPodServerless] No models found for subtype '{subtypeLocal}' in this attempt");
                                    tempModels[subtypeLocal] = new List<string>();
                                    tempRemoteModels[subtypeLocal] = new Dictionary<string, JObject>();
                                    return;
                                }
                                List<string> modelNames = [];
                                Dictionary<string, JObject> modelMetadata = [];
                                foreach (JToken fileToken in filesArray)
                                {
                                    if (fileToken is JObject fileObj)
                                    {
                                        string modelName = fileObj["name"]?.ToString();
                                        if (!string.IsNullOrWhiteSpace(modelName))
                                        {
                                            modelNames.Add(modelName);
                                            JObject data = (JObject)fileObj.DeepClone();
                                            data["local"] = false;
                                            data["subtype"] = subtypeLocal;
                                            modelMetadata[modelName] = data;
                                        }
                                    }
                                    else
                                    {
                                        string modelName = fileToken.ToString();
                                        if (!string.IsNullOrWhiteSpace(modelName))
                                        {
                                            modelNames.Add(modelName);
                                            modelMetadata[modelName] = new JObject
                                            {
                                                ["name"] = modelName,
                                                ["local"] = false,
                                                ["subtype"] = subtypeLocal
                                            };
                                        }
                                    }
                                }
                                tempModels[subtypeLocal] = modelNames;
                                tempRemoteModels[subtypeLocal] = modelMetadata;
                                Logs.Debug($"[RunPodServerless] Found {modelNames.Count} models for subtype '{subtypeLocal}'");
                            }
                            catch (Exception ex)
                            {
                                Logs.Verbose($"[RunPodServerless] ListModels failed for subtype '{subtypeLocal}': {ex.Message}");
                                tempModels[subtypeLocal] = new List<string>();
                                tempRemoteModels[subtypeLocal] = new Dictionary<string, JObject>();
                            }
                        }));
                    }

                    await Task.WhenAll(fetches);
                    int total = tempModels.Values.Sum(list => list.Count);
                    if (total > 0)
                    {
                        RemoteModels ??= new ConcurrentDictionary<string, Dictionary<string, JObject>>();
                        Models ??= new ConcurrentDictionary<string, List<string>>();
                        foreach (var kv in tempModels)
                        {
                            Models[kv.Key] = kv.Value;
                        }
                        foreach (var kv in tempRemoteModels)
                        {
                            RemoteModels[kv.Key] = kv.Value;
                        }
                        Logs.Debug($"[RunPodServerless] Model refresh complete: {total} models across {tempModels.Count} subtypes");
                        return;
                    }

                    // No models yet; check timeout and retry after 10 seconds
                    if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec)
                    {
                        throw new TimeoutException($"No models discovered within {maxWaitSec} seconds.");
                    }
                    AddLoadStatus("Waiting for Swarm to finish loading models on worker...");
                    await Task.Delay(retryIntervalMs);
                    // Optionally extend keepalive during long waits
                    _ = client.KeepAliveAsync(keepaliveDuration, 30);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec)
                    {
                        throw;
                    }
                    Logs.Verbose($"[RunPodServerless] Model refresh attempt failed: {ex.Message}. Retrying in 10s...");
                    await Task.Delay(retryIntervalMs);
                    _ = client.KeepAliveAsync(keepaliveDuration, 30);
                }
            }
        }
        finally
        {
            try
            {
                await client.ShutdownWorkerAsync();
                Logs.Debug("[RunPodServerless] Worker shutdown signal sent");
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Worker shutdown ignored: {ex.Message}");
            }
        }
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
            await WaitForWorkerBackendsLoadedAsync(client, workerInfo, Config.StartupTimeoutSec);
            // Ensure the model requested in this generation is selected on the worker
            string genModel = null;
            object mobj = user_input.Get(T2IParamTypes.Model);
            if (mobj is T2IModel tmm) { genModel = tmm.Name; }
            else if (mobj is string ms) { genModel = ms; }
            if (!string.IsNullOrWhiteSpace(genModel))
            {
                Logs.Debug($"[RunPodServerless] Ensuring model selected on worker before generation: {genModel}");
                JObject selReq = new()
                {
                    ["session_id"] = workerInfo.SessionId,
                    ["model"] = genModel
                };
                JObject selResp = await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/SelectModel", selReq, timeoutSeconds: 120);
                bool selOk = selResp.TryGetValue("success", out JToken sr) && sr.Value<bool>();
                if (!selOk)
                {
                    // Retry with alternate name if needed
                    string alt = genModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                        ? genModel[..^".safetensors".Length]
                        : genModel + ".safetensors";
                    if (!string.Equals(alt, genModel, StringComparison.OrdinalIgnoreCase))
                    {
                        Logs.Debug($"[RunPodServerless] Retry SelectModel with alt: {alt}");
                        selReq["model"] = alt;
                        selResp = await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/SelectModel", selReq, timeoutSeconds: 120);
                        selOk = selResp.TryGetValue("success", out sr) && sr.Value<bool>();
                        if (!selOk)
                        {
                            Logs.Warning($"[RunPodServerless] Failed to select model '{genModel}' (and alt '{alt}') before generation. Proceeding anyway.");
                        }
                        else
                        {
                            genModel = alt;
                        }
                    }
                }
            }
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
