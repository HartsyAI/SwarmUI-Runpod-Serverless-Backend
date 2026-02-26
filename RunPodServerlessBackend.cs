using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.RunPodServerless;
using Hartsy.Extensions.RunPodServerless.Models;
using Hartsy.Extensions.RunPodServerless.WebAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Net.Http;
using System.Net.WebSockets;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Backend for RunPod serverless GPU endpoints with on-demand worker lifecycle management.</summary>
public class RunPodServerlessBackend : AbstractT2IBackend
{
    /// <summary>Cache of remote models by subtype.</summary>
    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;

    public Session Session = null;

    /// <summary>Currently active worker info (if any).</summary>
    public WorkerInfo CurrentWorker = null;

    /// <summary>When the current worker's keepalive expires.</summary>
    public DateTime WorkerKeepaliveExpiry = DateTime.MinValue;

    /// <summary>Job ID of the active keepalive job (for cancellation on shutdown).</summary>
    public string ActiveKeepaliveJobId = null;

    /// <summary>A set of all supported features the remote worker has.</summary>
    public ConcurrentDictionary<string, string> RemoteFeatureCombo = new();

    /// <summary>Lock for worker state management.</summary>
    public SemaphoreSlim WorkerLock = new(1, 1);

    /// <summary>Exception thrown when a worker session becomes invalid.</summary>
    public class SessionInvalidException : Exception
    {
    }

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

    /// <summary>Auto-throw exception if response indicates session error.</summary>
    public static void AutoThrowException(JObject data)
    {
        if (data.TryGetValue("error_id", out JToken errorId) && errorId.ToString() == "invalid_session_id")
        {
            Logs.Verbose($"[RunPodServerless] AutoThrowException: detected invalid_session_id in response");
            throw new SessionInvalidException();
        }
        if (data.TryGetValue("error", out JToken error))
        {
            string err = error.ToString();
            Logs.Verbose($"[RunPodServerless] AutoThrowException: detected error in response: {err}");
            throw new SwarmReadableErrorException($"Remote worker gave error: {err}");
        }
    }

    /// <summary>Run an async task with automatic session refresh on invalidation. Recursively retries after re-creating the session.</summary>
    public async Task RunWithSession(Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (SessionInvalidException)
        {
            Logs.Verbose($"[RunPodServerless] Session invalid for backend {BackendData?.ID}, recreating session and retrying...");
            Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            await ClearWorkerStateAsync();
            await RunWithSession(run);
        }
    }

    /// <summary>Run an async task with automatic session refresh on invalidation (with return value). Recursively retries after re-creating the session.</summary>
    public async Task<T> RunWithSession<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (SessionInvalidException)
        {
            Logs.Verbose($"[RunPodServerless] Session invalid for backend {BackendData?.ID}, recreating session and retrying...");
            Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            await ClearWorkerStateAsync();
            return await RunWithSession(run);
        }
    }

    /// <summary>Retrieve the RunPod API key from backend config or user's stored keys.</summary>
    public string GetRunPodApiKey(Session session = null, T2IParamInput input = null)
    {
        Session sessData = session ?? Session;
        Logs.Verbose($"[RunPodServerless] GetRunPodApiKey: session={sessData?.ID}, user={sessData?.User?.UserID ?? "<null>"}");
        if (sessData?.User is null)
        {
            throw new SwarmReadableErrorException("RunPod API key not found. No user session is available. Please ensure you are logged in and have a RunPod API key configured in User Settings → API Keys.");
        }
        string apiKey = sessData.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new SwarmReadableErrorException($"RunPod API key is not configured for user '{sessData.User.UserID}'. Please set your RunPod API key in User Settings → API Keys → RunPod.");
        }
        Logs.Verbose($"[RunPodServerless] GetRunPodApiKey: retrieved key for user '{sessData.User.UserID}' (length: {apiKey.Length})");
        return apiKey;
    }

    public override async Task Init()
    {
        Logs.Verbose($"[RunPodServerless] Init() called for backend #{BackendData?.ID}");
        AddLoadStatus("Starting RunPod serverless backend...");
        if (string.IsNullOrWhiteSpace(Config.EndpointId))
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus("ERROR: RunPod Endpoint ID is not configured. Please set it in the backend settings.");
            Logs.Error("[RunPodServerless] Backend cannot start: Endpoint ID is empty. Configure it in the backend settings.");
            return;
        }
        Logs.Verbose($"[RunPodServerless] Init config: EndpointId={Config.EndpointId}, MaxConcurrent={Config.MaxConcurrent}, PollIntervalMs={Config.PollIntervalMs}, StartupTimeoutSec={Config.StartupTimeoutSec}, GenerationTimeoutSec={Config.GenerationTimeoutSec}, AutoRefresh={Config.AutoRefresh}");
        Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
        Logs.Verbose($"[RunPodServerless] Created internal session: {Session?.ID}");
        string apiKey;
        try
        {
            apiKey = GetRunPodApiKey(Session);
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {ex.Message}");
            Logs.Error($"[RunPodServerless] Backend cannot start: {ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus("ERROR: RunPod API key is not configured. Please set your API key in User Settings → API Keys.");
            Logs.Error("[RunPodServerless] Backend cannot start: RunPod API key is empty. Set it in User Settings → API Keys.");
            return;
        }
        MaxUsages = Math.Max(1, Config.MaxConcurrent);
        // Mark backend as RUNNING so the handler can select it and call LoadModel to wake workers on demand.
        Status = BackendStatus.RUNNING;
        CanLoadModels = true;
        Logs.Verbose($"[RunPodServerless] Backend #{BackendData?.ID} marked as RUNNING, CanLoadModels=true, MaxUsages={MaxUsages}");
        AddLoadStatus($"RunPod backend ready (endpoint: {Config.EndpointId}, max concurrent: {MaxUsages}).");
        if (Config.AutoRefresh)
        {
            Logs.Verbose($"[RunPodServerless] AutoRefresh enabled, starting background model refresh task...");
            _ = Utilities.RunCheckedTask(async () =>
            {
                try
                {
                    AddLoadStatus("Refreshing models from worker (background)...");
                    Logs.Verbose($"[RunPodServerless] Background model refresh task started for backend #{BackendData?.ID}");
                    await RefreshModelsFromWorkerAsync(Session);
                    int modelCount = Models?.Values.Sum(list => list.Count) ?? 0;
                    int remoteCount = RemoteModels?.Values.Sum(dict => dict.Count) ?? 0;
                    AddLoadStatus("Model refresh complete.");
                    Logs.Info($"[RunPodServerless] Model refresh complete. Loaded {modelCount} models ({remoteCount} remote metadata entries).");
                    Logs.Verbose($"[RunPodServerless] Model subtypes after refresh: {string.Join(", ", Models?.Keys ?? [])}");
                }
                catch (Exception ex)
                {
                    AddLoadStatus("Model refresh failed (background): " + ex.Message);
                    Logs.Warning($"[RunPodServerless] Background model refresh failed: {ex.Message}");
                    Logs.Verbose($"[RunPodServerless] Background model refresh exception details: {ex}");
                }
            });
        }
        else
        {
            Logs.Verbose($"[RunPodServerless] AutoRefresh disabled, skipping background model refresh.");
        }
    }

    /// <summary>Poll the worker's Swarm until all backends have finished loading. Also updates supported features from the worker.</summary>
    public async Task WaitForWorkerBackendsLoadedAsync(RunPodApiClient client, WorkerInfo worker, int timeoutSec)
    {
        int pollMs = Math.Clamp(Config.PollIntervalMs, 500, 5000);
        int attempts = Math.Max(1, (timeoutSec * 1000) / pollMs);
        Logs.Verbose($"[RunPodServerless] WaitForWorkerBackendsLoaded: polling {worker.PublicUrl} (max {attempts} attempts, {pollMs}ms interval, timeout {timeoutSec}s)");
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                Logs.Verbose($"[RunPodServerless] WaitForWorkerBackendsLoaded: poll attempt {i + 1}/{attempts}");
                JObject list = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListBackends", new JObject
                {
                    ["session_id"] = worker.SessionId,
                    ["nonreal"] = true,
                    ["full_data"] = true
                }, timeoutSeconds: 30);
                List<(string id, string status)> statuses = list.Properties().Select(p => p.Value).OfType<JObject>().Select(b => (id: b["id"]?.ToString(), status: b["status"]?.ToString())).ToList();
                bool anyLoading = statuses.Any(x => string.Equals(x.status, "loading", StringComparison.OrdinalIgnoreCase));
                Logs.Debug($"[RunPodServerless] Worker backend statuses: {string.Join(", ", statuses.Select(s => $"{s.id}:{s.status}"))}");
                UpdateFeaturesFromWorker(list);
                if (!anyLoading)
                {
                    Logs.Debug("[RunPodServerless] Worker backends are loaded.");
                    Logs.Verbose($"[RunPodServerless] All {statuses.Count} worker backends ready after {i + 1} poll(s)");
                    return;
                }
                Logs.Verbose($"[RunPodServerless] Some worker backends still loading, waiting {pollMs}ms before next poll...");
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Error while checking worker backend status (attempt {i + 1}): {ex.Message}");
            }
            await Task.Delay(pollMs);
        }
        Logs.Verbose("[RunPodServerless] Timed out waiting for worker backends to finish loading; proceeding anyway.");
    }

    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        Logs.Verbose($"[RunPodServerless] LoadModel called: model={model?.Name ?? "<null>"}, hasInput={input is not null}, backend=#{BackendData?.ID}");
        if (input is not null && input.Get(T2IParamTypes.NoLoadModels, false))
        {
            Logs.Verbose($"[RunPodServerless] LoadModel: NoLoadModels=true, skipping actual load for '{model?.Name}'");
            CurrentModelName = model?.Name;
            return true;
        }
        try
        {
            Session sess = input?.SourceSession ?? Session ?? Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            Logs.Verbose($"[RunPodServerless] LoadModel: using session {sess?.ID} (user: {sess?.User?.UserID ?? "<null>"})");
            string apiKey = GetRunPodApiKey(sess, input);
            RunPodApiClient client = new(apiKey, Config.EndpointId);
            int keepaliveDuration = Math.Max(180, Config.StartupTimeoutSec);
            Logs.Debug($"[RunPodServerless] LoadModel requested: {(model?.Name ?? "<null>")}");
            Logs.Verbose($"[RunPodServerless] LoadModel: keepaliveDuration={keepaliveDuration}s, endpoint={Config.EndpointId}");
            WorkerInfo worker = await GetOrWakeWorkerAsync(client, keepaliveDuration);
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
            async Task<bool> trySelect(string name)
            {
                Logs.Verbose($"[RunPodServerless] trySelect: attempting SelectModel for '{name}' on {worker.PublicUrl}");
                JObject req = new()
                {
                    ["session_id"] = worker.SessionId,
                    ["model"] = name
                };
                JObject resp = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/SelectModel", req, timeoutSeconds: 120);
                bool ok = resp.TryGetValue("success", out JToken s) && s.Value<bool>();
                if (ok)
                {
                    Logs.Verbose($"[RunPodServerless] trySelect: SelectModel succeeded for '{name}'");
                }
                else
                {
                    Logs.Verbose($"[RunPodServerless] SelectModel failed for '{name}', response: {resp.ToString(Formatting.None)}");
                }
                return ok;
            }
            static string ToggleSafetensors(string name)
            {
                return name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? name[..^".safetensors".Length] : name + ".safetensors";
            }
            string basename = desiredModel?.Contains('/') is true ? desiredModel[(desiredModel.LastIndexOf('/') + 1)..] : desiredModel;
            string[] candidates = new[]
            {
                desiredModel,
                ToggleSafetensors(desiredModel ?? ""),
                basename,
                ToggleSafetensors(basename ?? "")
            }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Logs.Verbose($"[RunPodServerless] SelectModel candidates: {string.Join(", ", candidates)}");
            foreach (string cand in candidates)
            {
                if (await trySelect(cand))
                {
                    desiredModel = cand;
                    CurrentModelName = desiredModel;
                    Logs.Debug($"[RunPodServerless] Model selected on worker successfully: {desiredModel}");
                    return true;
                }
            }
            try
            {
                JObject listReq = new()
                {
                    ["session_id"] = worker.SessionId,
                    ["path"] = "",
                    ["depth"] = 999,
                    ["subtype"] = "Stable-Diffusion",
                    ["allowRemote"] = false,
                    ["sortBy"] = "Name",
                    ["sortReverse"] = false,
                    ["dataImages"] = false
                };
                JObject listResp = await client.CallSwarmUIAsync(worker.PublicUrl, "/API/ListModels", listReq, timeoutSeconds: 60);
                List<string> files = (listResp["files"] as JArray)?.Select(t => t.Type == JTokenType.Object ? t["name"]?.ToString() ?? t.ToString() : t.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
                Logs.Verbose($"[RunPodServerless] Worker has {files.Count} models for subtype 'Stable-Diffusion': {string.Join(", ", files)}");
                string dl = desiredModel ?? "";
                string dlBase = basename ?? dl;
                string best = files.FirstOrDefault(n => n.Equals(dl, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault(n => n.Equals(dlBase, StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault(n => n.EndsWith(dlBase, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault(n => n.Contains(dlBase, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(best))
                {
                    Logs.Debug($"[RunPodServerless] Retrying SelectModel with worker-listed name: {best}");
                    if (await trySelect(best))
                    {
                        desiredModel = best;
                        CurrentModelName = desiredModel;
                        Logs.Debug($"[RunPodServerless] Model selected on worker successfully: {desiredModel}");
                        return true;
                    }
                }
                else
                {
                    Logs.Warning($"[RunPodServerless] Requested model '{desiredModel}' not found in worker list. Available models: {string.Join(", ", files)}");
                }
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Failed to query worker model list for fallback: {ex.Message}");
            }
            Logs.Warning($"[RunPodServerless] Unable to select model '{desiredModel}' on worker after all retries.");
            return false;
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
        Logs.Verbose($"[RunPodServerless] RefreshModelsFromWorkerAsync called for backend #{BackendData?.ID}");
        string apiKey = GetRunPodApiKey(session);
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        int retryIntervalMs = 10_000; // 10 seconds between attempts
        int maxWaitSec = Math.Max(Config.StartupTimeoutSec, 120);
        int keepaliveDuration = 180; // 3 minutes — GetOrWakeWorkerAsync will extend
        Logs.Debug($"[RunPodServerless] Starting worker for model refresh (keepalive: {keepaliveDuration}s)...");
        Logs.Verbose($"[RunPodServerless] RefreshModels config: maxWaitSec={maxWaitSec}, retryInterval={retryIntervalMs}ms, keepalive={keepaliveDuration}s");
        WorkerInfo worker = await GetOrWakeWorkerAsync(client, keepaliveDuration);
        Logs.Debug($"[RunPodServerless] Worker ready: {worker.WorkerId} at {worker.PublicUrl}");
        Logs.Verbose($"[RunPodServerless] Worker details: publicUrl={worker.PublicUrl}, sessionId={worker.SessionId?[..Math.Min(16, worker.SessionId?.Length ?? 0)]}..., version={worker.Version}");
        DateTime start = DateTime.UtcNow;
        Exception lastError = null;
        try
        {
            while (true)
            {
                try
                {
                    ConcurrentDictionary<string, List<string>> tempModels = new();
                    ConcurrentDictionary<string, Dictionary<string, JObject>> tempRemoteModels = new();
                    List<Task> fetches = [];
                    Logs.Verbose($"[RunPodServerless] Fetching models for {Program.T2IModelSets.Keys.Count()} subtypes: {string.Join(", ", Program.T2IModelSets.Keys)}");
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
                                            data["loaded"] ??= false;
                                            data["standard_width"] ??= 0;
                                            data["standard_height"] ??= 0;
                                            data["architecture"] ??= "";
                                            data["title"] ??= modelName.AfterLast('/');
                                            data["description"] ??= "";
                                            data["preview_image"] ??= "imgs/model_placeholder.jpg";
                                            data["is_supported_model_format"] ??= true;
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
                                                ["subtype"] = subtypeLocal,
                                                ["loaded"] = false,
                                                ["standard_width"] = 0,
                                                ["standard_height"] = 0,
                                                ["architecture"] = "",
                                                ["title"] = modelName.AfterLast('/'),
                                                ["description"] = "",
                                                ["preview_image"] = "imgs/model_placeholder.jpg",
                                                ["is_supported_model_format"] = true
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
                    int sdCount = tempModels.TryGetValue("Stable-Diffusion", out List<string> sdList) ? sdList.Count : 0;
                    Logs.Verbose($"[RunPodServerless] Model fetch round complete: {total} models found across {tempModels.Count} subtypes ({sdCount} Stable-Diffusion)");
                    if (sdCount > 0)
                    {
                        RemoteModels ??= new ConcurrentDictionary<string, Dictionary<string, JObject>>();
                        Models ??= new ConcurrentDictionary<string, List<string>>();
                        foreach (KeyValuePair<string, List<string>> kv in tempModels)
                        {
                            Models[kv.Key] = kv.Value;
                            Logs.Verbose($"[RunPodServerless] Registered {kv.Value.Count} models for subtype '{kv.Key}': {string.Join(", ", kv.Value)}");
                        }
                        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in tempRemoteModels)
                        {
                            RemoteModels[kv.Key] = kv.Value;
                        }
                        Logs.Debug($"[RunPodServerless] Model refresh complete: {total} models across {tempModels.Count} subtypes");
                        Logs.Verbose($"[RunPodServerless] RemoteModels now has {RemoteModels.Values.Sum(d => d.Count)} total metadata entries");
                        Program.ModelRefreshEvent?.Invoke();
                        return;
                    }
                    int elapsedSec = (int)(DateTime.UtcNow - start).TotalSeconds;
                    if (elapsedSec >= maxWaitSec)
                    {
                        throw new TimeoutException($"No Stable-Diffusion models discovered within {maxWaitSec} seconds ({total} other models found).");
                    }
                    Logs.Verbose($"[RunPodServerless] No Stable-Diffusion models found yet ({sdCount} SD, {total} total, {elapsedSec}s elapsed, max {maxWaitSec}s), retrying in {retryIntervalMs}ms...");
                    AddLoadStatus("Waiting for Swarm to finish loading models on worker...");
                    await Task.Delay(retryIntervalMs);
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
                }
            }
        }
        finally
        {
            Logs.Debug($"[RunPodServerless] Model refresh finished. Worker {CurrentWorker?.WorkerId ?? "<null>"} remains cached for subsequent operations.");
        }
    }

    /// <inheritdoc/>
    public override async Task Shutdown()
    {
        Logs.Info($"[RunPodServerless] Backend {BackendData?.ID} shutting down...");
        Logs.Verbose($"[RunPodServerless] Shutdown state: CurrentWorker={CurrentWorker?.WorkerId ?? "<null>"}, ActiveKeepaliveJobId={ActiveKeepaliveJobId ?? "<null>"}, WorkerKeepaliveExpiry={WorkerKeepaliveExpiry}");
        if (CurrentWorker != null || !string.IsNullOrEmpty(ActiveKeepaliveJobId))
        {
            try
            {
                string apiKey = GetRunPodApiKey(Session);
                RunPodApiClient client = new(apiKey, Config.EndpointId);
                if (!string.IsNullOrEmpty(ActiveKeepaliveJobId))
                {
                    Logs.Debug($"[RunPodServerless] Cancelling keepalive job {ActiveKeepaliveJobId}...");
                    await client.CancelJobAsync(ActiveKeepaliveJobId);
                    Logs.Verbose($"[RunPodServerless] Keepalive cancellation request sent for {ActiveKeepaliveJobId}");
                }
                else
                {
                    Logs.Verbose($"[RunPodServerless] No active keepalive job to cancel");
                }
            }
            catch (Exception ex)
            {
                Logs.Verbose($"[RunPodServerless] Shutdown cleanup failed (may already be down): {ex.Message}");
            }
            await ClearWorkerStateAsync();
            Logs.Verbose($"[RunPodServerless] Worker state cleared after shutdown");
        }
        else
        {
            Logs.Verbose($"[RunPodServerless] No active worker or keepalive, nothing to clean up");
        }
        Status = BackendStatus.DISABLED;
        Logs.Verbose($"[RunPodServerless] Backend #{BackendData?.ID} status set to DISABLED");
    }

    /// <inheritdoc/>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        Logs.Verbose($"[RunPodServerless] Generate called on backend #{BackendData?.ID}, user={user_input.SourceSession?.User?.UserID ?? "<null>"}");
        if (!user_input.SourceSession.User.HasPermission(RunPodPermissions.PermUseRunPod))
        {
            throw new SwarmReadableErrorException("You do not have permission to use RunPod Serverless backends.");
        }
        string apiKey = GetRunPodApiKey(user_input.SourceSession, user_input);
        if (string.IsNullOrEmpty(Config.EndpointId))
        {
            throw new SwarmReadableErrorException("RunPod Endpoint ID is not configured. Please set it in the backend settings.");
        }
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        try
        {
            Logs.Debug($"[RunPodServerless] Starting generation on endpoint {Config.EndpointId}");
            int keepaliveDuration = 180;
            Logs.Verbose($"[RunPodServerless] Generate: keepaliveDuration={keepaliveDuration}s, generationTimeout={Config.GenerationTimeoutSec}s");
            WorkerInfo workerInfo = await GetOrWakeWorkerAsync(client, keepaliveDuration);
            Logs.Debug($"[RunPodServerless] Worker ready: {workerInfo.WorkerId} at {workerInfo.PublicUrl}");
            Logs.Verbose($"[RunPodServerless] Generate: worker session={workerInfo.SessionId?[..Math.Min(16, workerInfo.SessionId?.Length ?? 0)]}...");
            await WaitForWorkerBackendsLoadedAsync(client, workerInfo, Config.StartupTimeoutSec);
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
                    string alt = genModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? genModel[..^".safetensors".Length] : genModel + ".safetensors";
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
            Logs.Verbose($"[RunPodServerless] Generate request keys: {string.Join(", ", swarmRequest.Properties().Select(p => p.Name))}");
            JObject swarmResponse = await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/GenerateText2Image", swarmRequest, timeoutSeconds: Config.GenerationTimeoutSec);
            Logs.Verbose($"[RunPodServerless] Generate response keys: {string.Join(", ", swarmResponse.Properties().Select(p => p.Name))}");
            Image[] images = ExtractGeneratedImages(swarmResponse);
            if (images.Length is 0)
            {
                Logs.Verbose($"[RunPodServerless] Generate: no images in response. Full response: {swarmResponse.ToString(Formatting.None)}");
                throw new Exception("No images returned from generation");
            }
            Logs.Debug($"[RunPodServerless] Generated {images.Length} image(s) successfully");
            return images;
        }
        catch (SwarmReadableErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodServerless] Generation failed: {ex.Message}");
            throw new SwarmReadableErrorException($"RunPod generation failed: {ex.Message}");
        }
    }

    /// <summary>Build generation request from T2IParamInput - uses standard ToJSON() like SwarmSwarmBackend.</summary>
    public JObject BuildGenerationRequest(T2IParamInput input, string sessionId)
    {
        Logs.Verbose($"[RunPodServerless] BuildGenerationRequest: sessionId={sessionId?[..Math.Min(16, sessionId?.Length ?? 0)]}...");
        input.ProcessPromptEmbeds(x => $"<embedding:{x}>");
        JObject request = input.ToJSON();
        Logs.Verbose($"[RunPodServerless] BuildGenerationRequest: base request has {request.Properties().Count()} properties");
        request["session_id"] = sessionId;
        request[T2IParamTypes.Images.Type.ID] = 1;
        request[T2IParamTypes.DoNotSave.Type.ID] = true;
        request.Remove(T2IParamTypes.ExactBackendID.Type.ID);
        request.Remove(T2IParamTypes.BackendType.Type.ID);
        if (input.ReceiveRawBackendData is not null)
        {
            request[T2IParamTypes.ForwardRawBackendData.Type.ID] = true;
            Logs.Verbose($"[RunPodServerless] BuildGenerationRequest: forwarding raw backend data");
        }
        request[T2IParamTypes.ForwardSwarmData.Type.ID] = true;
        Logs.Verbose($"[RunPodServerless] BuildGenerationRequest: final request has {request.Properties().Count()} properties");
        return request;
    }

    /// <summary>Extract images from SwarmUI API response using standard format.</summary>
    public Image[] ExtractGeneratedImages(JObject response)
    {
        List<Image> images = [];
        JArray imageArray = response["images"] as JArray;
        if (imageArray is null || imageArray.Count is 0)
        {
            Logs.Verbose($"[RunPodServerless] ExtractGeneratedImages: no 'images' array in response (keys: {string.Join(", ", response.Properties().Select(p => p.Name))})");
            return [];
        }
        Logs.Verbose($"[RunPodServerless] ExtractGeneratedImages: processing {imageArray.Count} image token(s)");
        foreach (JToken imageToken in imageArray)
        {
            try
            {
                string dataStr = imageToken.ToString();
                Logs.Verbose($"[RunPodServerless] ExtractGeneratedImages: decoding image ({dataStr.Length} chars)");
                Image img = ImageFile.FromDataString(dataStr) as Image;
                images.Add(img);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[RunPodServerless] Failed to decode image: {ex.Message}");
            }
        }
        Logs.Verbose($"[RunPodServerless] ExtractGeneratedImages: decoded {images.Count}/{imageArray.Count} images successfully");
        return [.. images];
    }

    // TODO: Implement queue pressure detection — if the job queue stays long for a sustained period,
    // spin up an additional worker to relieve the load. Needs a strategy to measure queue depth over
    // time (not just a momentary spike) before committing to a new worker.

    /// <summary>Get active worker or wake up a new one. Thread-safe with state tracking.</summary>
    public async Task<WorkerInfo> GetOrWakeWorkerAsync(RunPodApiClient client, int keepaliveDuration)
    {
        Logs.Verbose($"[RunPodServerless] GetOrWakeWorkerAsync: acquiring WorkerLock (keepaliveDuration={keepaliveDuration}s)...");
        await WorkerLock.WaitAsync();
        try
        {
            Logs.Verbose($"[RunPodServerless] GetOrWakeWorkerAsync: WorkerLock acquired. CurrentWorker={CurrentWorker?.WorkerId ?? "<null>"}, KeepaliveExpiry={WorkerKeepaliveExpiry}, ActiveKeepaliveJobId={ActiveKeepaliveJobId ?? "<null>"}");
            if (CurrentWorker is not null && DateTime.UtcNow < WorkerKeepaliveExpiry)
            {
                int remainingSeconds = (int)(WorkerKeepaliveExpiry - DateTime.UtcNow).TotalSeconds;
                Logs.Debug($"[RunPodServerless] Reusing active worker {CurrentWorker.WorkerId} (expires in {remainingSeconds}s)");
                if (remainingSeconds < keepaliveDuration / 2)
                {
                    Logs.Debug($"[RunPodServerless] Extending keepalive by {keepaliveDuration}s (remaining {remainingSeconds}s < threshold {keepaliveDuration / 2}s)");
                    Logs.Verbose($"[RunPodServerless] Submitting new keepalive job in background (old job: {ActiveKeepaliveJobId ?? "<null>"})");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string newJobId = await client.SubmitKeepaliveAsync(keepaliveDuration, 30);
                            Logs.Verbose($"[RunPodServerless] Keepalive extension submitted: {newJobId} (replacing {ActiveKeepaliveJobId ?? "<null>"})");
                            ActiveKeepaliveJobId = newJobId;
                        }
                        catch (Exception ex)
                        {
                            Logs.Verbose($"[RunPodServerless] Keepalive extension failed: {ex.Message}");
                        }
                    });
                    WorkerKeepaliveExpiry = DateTime.UtcNow.AddSeconds(keepaliveDuration);
                }
                else
                {
                    Logs.Verbose($"[RunPodServerless] No keepalive extension needed (remaining {remainingSeconds}s >= threshold {keepaliveDuration / 2}s)");
                }
                return CurrentWorker;
            }
            if (CurrentWorker is not null)
            {
                Logs.Verbose($"[RunPodServerless] Previous worker {CurrentWorker.WorkerId} has expired (expiry was {WorkerKeepaliveExpiry}), waking new worker");
            }
            else
            {
                Logs.Verbose($"[RunPodServerless] No cached worker, waking new worker");
            }
            Logs.Debug($"[RunPodServerless] No active worker, waking up new worker (keepalive: {keepaliveDuration}s)");
            CurrentWorker = await WakeupAndWaitForWorkerAsync(client, keepaliveDuration);
            WorkerKeepaliveExpiry = DateTime.UtcNow.AddSeconds(keepaliveDuration);
            Logs.Verbose($"[RunPodServerless] New worker cached: {CurrentWorker.WorkerId}, expiry set to {WorkerKeepaliveExpiry}");
            return CurrentWorker;
        }
        finally
        {
            WorkerLock.Release();
            Logs.Verbose($"[RunPodServerless] GetOrWakeWorkerAsync: WorkerLock released");
        }
    }

    /// <summary>Clear the current worker state (call when shutting down worker).</summary>
    public async Task ClearWorkerStateAsync()
    {
        Logs.Verbose($"[RunPodServerless] ClearWorkerStateAsync: clearing worker={CurrentWorker?.WorkerId ?? "<null>"}, keepaliveJob={ActiveKeepaliveJobId ?? "<null>"}");
        await WorkerLock.WaitAsync();
        try
        {
            CurrentWorker = null;
            WorkerKeepaliveExpiry = DateTime.MinValue;
            ActiveKeepaliveJobId = null;
            Logs.Verbose($"[RunPodServerless] ClearWorkerStateAsync: all worker state cleared");
        }
        finally
        {
            WorkerLock.Release();
        }
    }

    /// <summary>Wake up a RunPod worker and wait for connection info.
    /// 1. Submit wakeup job via /run (handler returns quickly with public_url, session_id, worker_id).
    /// 2. Poll job status via GET /status (read-only, no new jobs created) until COMPLETED.
    /// 3. Extract connection info from the completed job output.
    /// 4. Submit a separate keepalive job to keep the worker alive.
    /// Total RunPod jobs created: exactly 2 (wakeup + keepalive).</summary>
    public async Task<WorkerInfo> WakeupAndWaitForWorkerAsync(RunPodApiClient client, int keepaliveDuration)
    {
        int maxWaitSeconds = Config.StartupTimeoutSec;
        int pollIntervalMs = Math.Clamp(Config.PollIntervalMs, 1000, 10000);
        string wakeupJobId = await client.WakeupWorkerAsync();
        Logs.Info($"[RunPodServerless] Wakeup job {wakeupJobId} submitted. Waiting for worker (max {maxWaitSeconds}s, poll every {pollIntervalMs}ms)...");
        Logs.Verbose($"[RunPodServerless] WakeupAndWait: starting status polling for job {wakeupJobId}...");
        JObject output = await client.WaitForJobAsync(wakeupJobId, pollIntervalMs, maxWaitSeconds);
        Logs.Verbose($"[RunPodServerless] WakeupAndWait: job completed. Output keys: {string.Join(", ", output.Properties().Select(p => p.Name))}");
        string publicUrl = output["public_url"]?.ToString();
        string sessionId = output["session_id"]?.ToString();
        string workerId = output["worker_id"]?.ToString();
        string version = output["version"]?.ToString();
        Logs.Verbose($"[RunPodServerless] WakeupAndWait: extracted publicUrl={publicUrl}, workerId={workerId}, version={version}, sessionId={sessionId?[..Math.Min(16, sessionId?.Length ?? 0)]}...");
        if (string.IsNullOrEmpty(publicUrl) || string.IsNullOrEmpty(sessionId))
        {
            Logs.Warning($"[RunPodServerless] Wakeup output missing required fields. Output: {output}");
            throw new Exception("Wakeup job completed but did not return public_url/session_id.");
        }
        Logs.Info($"[RunPodServerless] Worker ready: {workerId} at {publicUrl} (session: {sessionId?[..Math.Min(16, sessionId.Length)]}...)");
        try
        {
            ActiveKeepaliveJobId = await client.SubmitKeepaliveAsync(keepaliveDuration, 30);
            Logs.Debug($"[RunPodServerless] Keepalive job submitted: {ActiveKeepaliveJobId} (duration: {keepaliveDuration}s)");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodServerless] Failed to submit keepalive job (worker may scale down early): {ex.Message}");
        }
        return new WorkerInfo
        {
            PublicUrl = publicUrl,
            SessionId = sessionId,
            WorkerId = workerId,
            Version = version
        };
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        Logs.Verbose($"[RunPodServerless] GenerateLive called on backend #{BackendData?.ID}, batchId={batchId}, user={user_input.SourceSession?.User?.UserID ?? "<null>"}");
        if (!user_input.SourceSession.User.HasPermission(RunPodPermissions.PermUseRunPod))
        {
            throw new SwarmReadableErrorException("You do not have permission to use RunPod Serverless backends.");
        }
        string apiKey = GetRunPodApiKey(user_input.SourceSession, user_input);
        if (string.IsNullOrEmpty(Config.EndpointId))
        {
            throw new SwarmReadableErrorException("RunPod Endpoint ID is not configured. Please set it in the backend settings.");
        }
        RunPodApiClient client = new(apiKey, Config.EndpointId);
        try
        {
            Logs.Debug($"[RunPodServerless] Starting live generation on endpoint {Config.EndpointId}");
            int keepaliveDuration = 300; // Longer keepalive for live generation
            Logs.Verbose($"[RunPodServerless] GenerateLive: keepaliveDuration={keepaliveDuration}s");
            WorkerInfo workerInfo = await GetOrWakeWorkerAsync(client, keepaliveDuration);
            Logs.Debug($"[RunPodServerless] Worker ready for live generation: {workerInfo.WorkerId} at {workerInfo.PublicUrl}");
            Logs.Verbose($"[RunPodServerless] GenerateLive: worker session={workerInfo.SessionId?[..Math.Min(16, workerInfo.SessionId?.Length ?? 0)]}...");
            await WaitForWorkerBackendsLoadedAsync(client, workerInfo, Config.StartupTimeoutSec);
            string genModel = null;
            object mobj = user_input.Get(T2IParamTypes.Model);
            if (mobj is T2IModel tmm)
            {
                genModel = tmm.Name;
            }
            else if (mobj is string ms)
            {
                genModel = ms;
            }
            if (!string.IsNullOrWhiteSpace(genModel))
            {
                Logs.Debug($"[RunPodServerless] Ensuring model selected on worker before live generation: {genModel}");
                JObject selReq = new()
                {
                    ["session_id"] = workerInfo.SessionId,
                    ["model"] = genModel
                };
                JObject selResp = await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/SelectModel", selReq, timeoutSeconds: 120);
                bool selOk = selResp.TryGetValue("success", out JToken sr) && sr.Value<bool>();
                if (!selOk)
                {
                    Logs.Warning($"[RunPodServerless] Failed to select model '{genModel}' before live generation. Proceeding anyway.");
                }
            }
            JObject swarmRequest = BuildGenerationRequest(user_input, workerInfo.SessionId);
            Logs.Verbose($"[RunPodServerless] GenerateLive: connecting WebSocket to {workerInfo.PublicUrl}/API/GenerateText2ImageWS");
            await RunWithSession(async () =>
            {
                ClientWebSocket websocket = await NetworkBackendUtils.ConnectWebsocket(workerInfo.PublicUrl, "API/GenerateText2ImageWS", ws => { /* No special headers needed for worker connection */ } );
                Logs.Verbose($"[RunPodServerless] GenerateLive: WebSocket connected, sending generation request...");
                await websocket.SendJson(swarmRequest, API.WebsocketTimeout);
                Logs.Verbose($"[RunPodServerless] GenerateLive: request sent, waiting for responses...");
                while (true)
                {
                    if (user_input.InterruptToken.IsCancellationRequested)
                    {
                        Logs.Verbose($"[RunPodServerless] GenerateLive: interrupt requested, sending InterruptAll to worker");
                        JObject interruptReq = new()
                        {
                            ["session_id"] = workerInfo.SessionId,
                            ["other_sessions"] = false
                        };
                        await client.CallSwarmUIAsync(workerInfo.PublicUrl, "/API/InterruptAll", interruptReq, timeoutSeconds: 30);
                    }
                    JObject response = await websocket.ReceiveJson(Utilities.ExtraLargeMaxReceive, true);
                    if (response is not null)
                    {
                        AutoThrowException(response);
                        if (response.TryGetValue("gen_progress", out JToken val) && val is JObject objVal)
                        {
                            if (objVal.ContainsKey("preview"))
                            {
                                Logs.Verbose($"[RunPodServerless] Got progress image from websocket {batchId}");
                            }
                            else
                            {
                                Logs.Verbose($"[RunPodServerless] Got progress from websocket for {batchId}");
                            }
                            string actualId = batchId;
                            if (objVal.TryGetValue("batch_index", out JToken batchIndRemote) && int.TryParse($"{batchIndRemote}", out int batchIndRemoteParsed) && batchIndRemoteParsed > 0 && int.TryParse(batchId, out int localInd))
                            {
                                actualId = $"{localInd + batchIndRemoteParsed}";
                            }
                            objVal["batch_index"] = actualId;
                            objVal["request_id"] = $"{user_input.UserRequestId}";
                            takeOutput(objVal);
                        }
                        else if (response.TryGetValue("image", out val))
                        {
                            Logs.Verbose($"[RunPodServerless] Got image from websocket");
                            takeOutput(ImageFile.FromDataString(val.ToString()));
                        }
                        else if (response.TryGetValue("raw_backend_data", out JToken rawData))
                        {
                            string type = rawData["type"]?.ToString();
                            string datab64 = rawData["data"]?.ToString();
                            Logs.Verbose($"[RunPodServerless] GenerateLive: got raw_backend_data type={type}, dataLength={datab64?.Length ?? 0}");
                            if (type is not null && datab64 is not null)
                            {
                                byte[] data = Convert.FromBase64String(datab64);
                                user_input.ReceiveRawBackendData?.Invoke(type, data);
                            }
                        }
                        else if (response.TryGetValue("raw_swarm_data", out JToken rawSwarmDataTok) && rawSwarmDataTok is JObject rawSwarmData)
                        {
                            Logs.Verbose($"[RunPodServerless] Got raw swarm data from websocket");
                            if (rawSwarmData.TryGetValue("params_used", out JToken paramsUsed))
                            {
                                foreach (JToken paramUsed in paramsUsed)
                                {
                                    user_input.ParamsQueried.Add($"{paramUsed}");
                                }
                            }
                            if (user_input.Get(T2IParamTypes.ForwardSwarmData, false))
                            {
                                takeOutput(response);
                            }
                        }
                        else
                        {
                            Logs.Verbose($"[RunPodServerless] Got other from websocket: {response}");
                        }
                    }
                    if (websocket.CloseStatus.HasValue)
                    {
                        break;
                    }
                }
                Logs.Verbose($"[RunPodServerless] GenerateLive: WebSocket closed (status: {websocket.CloseStatus}, desc: {websocket.CloseStatusDescription})");
                await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Program.GlobalProgramCancel);
            });
            Logs.Debug($"[RunPodServerless] Live generation completed successfully");
        }
        catch (SwarmReadableErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodServerless] Live generation failed: {ex.Message}");
            throw new SwarmReadableErrorException($"RunPod live generation failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures => RemoteFeatureCombo.IsEmpty ? ["text2image"] : RemoteFeatureCombo.Keys;

    /// <summary>Update supported features from remote worker backend data.</summary>
    public void UpdateFeaturesFromWorker(JObject backendData)
    {
        HashSet<string> features = ["text2image"];
        foreach (JToken backend in backendData.Values())
        {
            string status = backend["status"]?.ToString();
            if (status is "running" && backend["features"] is JArray featureArr)
            {
                features.UnionWith(featureArr.Select(f => f.ToString()));
            }
        }
        Logs.Verbose($"[RunPodServerless] UpdateFeaturesFromWorker: discovered {features.Count} features from worker: {string.Join(", ", features)}");
        int added = 0, removed = 0;
        foreach (string str in features.Where(f => !RemoteFeatureCombo.ContainsKey(f)))
        {
            RemoteFeatureCombo.TryAdd(str, str);
            added++;
        }
        foreach (string str in RemoteFeatureCombo.Keys.Where(f => !features.Contains(f)))
        {
            RemoteFeatureCombo.TryRemove(str, out _);
            removed++;
        }
        if (added > 0 || removed > 0)
        {
            Logs.Verbose($"[RunPodServerless] UpdateFeaturesFromWorker: added {added}, removed {removed}. Current features: {string.Join(", ", RemoteFeatureCombo.Keys)}");
        }
    }
}
