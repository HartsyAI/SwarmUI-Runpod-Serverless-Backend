using FreneticUtilities.FreneticExtensions;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Web API routes for the RunPod Serverless backend extension.</summary>
public static class RunPodWebAPI
{
    /// <summary>Register all RunPod Web API routes.</summary>
    public static void Register()
    {
        // POST /API/RunPod/RefreshModels - Trigger a manual model refresh by pinging a worker
        API.RegisterAPICall(RefreshModels, true, RunPodPermissions.PermUseRunPod);
        
        // GET /API/RunPod/GetStatus - Get current status of all RunPod backends
        API.RegisterAPICall(GetStatus, false, RunPodPermissions.PermUseRunPod);
        
        Logs.Verbose("[RunPodWebAPI] Registered API routes.");
    }

    /// <summary>Refresh models from a temporary worker for all running RunPod backends.</summary>
    public static async Task<JObject> RefreshModels(Session session)
    {
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            int refreshed = 0;
            int failed = 0;
            
            foreach (RunPodServerlessBackend backend in backends)
            {
                try
                {
                    Logs.Info($"[RunPodWebAPI] Refreshing models from worker for backend #{backend.BackendData.ID}...");
                    await backend.RefreshModelsFromWorkerAsync(session);
                    refreshed++;
                }
                catch (Exception ex)
                {
                    Logs.Error($"[RunPodWebAPI] Failed to refresh backend #{backend.BackendData.ID}: {ex.Message}");
                    failed++;
                }
            }

            return new JObject()
            {
                ["success"] = true,
                ["refreshed"] = refreshed,
                ["failed"] = failed,
                ["message"] = $"Refreshed {refreshed} RunPod backend(s), {failed} failed."
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodWebAPI] Error in RefreshModels: {ex.ReadableString()}");
            return new JObject()
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Get status information for all RunPod backends.</summary>
    public static async Task<JObject> GetStatus(Session session)
    {
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            JArray backendStatuses = new();

            foreach (RunPodServerlessBackend backend in backends)
            {
                backendStatuses.Add(new JObject()
                {
                    ["id"] = backend.BackendData.ID,
                    ["title"] = backend.Title,
                    ["status"] = backend.Status.ToString(),
                    ["endpoint_id"] = backend.Config.EndpointId,
                    ["model_count"] = Program.MainSDModels.Models.Keys.Count(k => k.StartsWith("RunPod/"))
                });
            }

            return new JObject()
            {
                ["success"] = true,
                ["backends"] = backendStatuses
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodWebAPI] Error in GetStatus: {ex.ReadableString()}");
            return new JObject()
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }
}
