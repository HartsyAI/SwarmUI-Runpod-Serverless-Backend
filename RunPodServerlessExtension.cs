using System.Linq;
using System.Collections.Generic;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using Microsoft.AspNetCore.Html;
using Hartsy.Extensions.RunPodServerless.WebAPI;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Permissions for the RunPod Serverless extension.</summary>
public static class RunPodPermissions
{
    public static readonly PermInfoGroup RunPodPermGroup = new("RunPodServerless", "Permissions related to RunPod serverless GPU backends.");
    public static readonly PermInfo PermUseRunPod = Permissions.Register(new PermInfo("use_runpod_serverless", "Use RunPod Serverless",
        "Allows using RunPod's serverless GPU endpoints for image generation.", PermissionDefault.POWERUSERS, RunPodPermGroup));
}

/// <summary>RunPod Serverless Backend Extension - Provides serverless GPU inference via RunPod.</summary>
public class RunPodServerlessExtension : Extension
{
    public override void OnPreInit()
    {
        Logs.Init("Initializing Hartsy's RunPod Serverless Backend Extension...");
    }

    public override void OnInit()
    {
        Logs.Verbose("[RunPodServerless] OnInit: registering backend type 'runpod_serverless'...");
        Program.Backends.RegisterBackendType<RunPodServerlessBackend>("runpod_serverless", "RunPod Serverless",
            "Serverless GPU inference via RunPod with direct SwarmUI API access. Supports on-demand scaling and cost-effective generation.",
            CanLoadFast: true);
        Logs.Verbose("[RunPodServerless] OnInit: adding 'runpod_api' to AcceptedAPIKeyTypes...");
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("runpod_api");
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey("runpod_api"))
            {
                UserUpstreamApiKeys.Register(new UserUpstreamApiKeys.ApiKeyInfo(KeyType: "runpod_api", JSPrefix: "runpod", Title: "RunPod",
                    CreateLink: "https://www.runpod.io/console/user/settings",
                    InfoHtml: new HtmlString("Enter your RunPod API key to use RunPod Serverless backends. Get your API key from <a href='https://www.runpod.io/console/user/settings' target='_blank'>RunPod Settings</a>.")
                ));
                Logs.Debug("Registered RunPod API key type in user settings.");
            }
            else
            {
                Logs.Verbose("RunPod API key type already registered.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to register RunPod API key type: {ex.Message}");
        }
        // Register extra models provider so RunPod remote models appear in model listings (like SwarmSwarm backend)
        try
        {
            if (!ModelsAPI.ExtraModelProviders.ContainsKey("runpod_serverless"))
            {
                ModelsAPI.ExtraModelProviders["runpod_serverless"] = (string subtype) =>
                {
                    RunPodServerlessBackend[] backs = [.. Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>().Where(b => b.RemoteModels is not null)];
                    Logs.Verbose($"[RunPodServerless] ExtraModelProviders queried for subtype '{subtype}': {backs.Length} backend(s) with RemoteModels");
                    IEnumerable<Dictionary<string, Newtonsoft.Json.Linq.JObject>> sets = backs.Select(b => b.RemoteModels.GetValueOrDefault(subtype)).Where(s => s is not null);
                    if (!sets.Any())
                    {
                        Logs.Verbose($"[RunPodServerless] ExtraModelProviders: no models found for subtype '{subtype}'");
                        return [];
                    }
                    var result = sets.Aggregate((a, b) => a.Union(b).PairsToDictionary(false));
                    Logs.Verbose($"[RunPodServerless] ExtraModelProviders: returning {result.Count} model(s) for subtype '{subtype}': {string.Join(", ", result.Keys)}");
                    return result;
                };
                Logs.Debug("Registered RunPod Serverless models provider for extra remote models.");
            }
            else
            {
                Logs.Verbose("RunPod Serverless models provider already registered.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to register RunPod extra models provider: {ex.Message}");
        }
        // Prefer RunPod only when the requested model is available on a RunPod worker
        try
        {
            T2IEngine.PreGenerateEvent += (p) =>
            {
                string currentType = p.UserInput.Get(T2IParamTypes.BackendType, "Any");
                if (!string.IsNullOrEmpty(currentType) && !currentType.Equals("Any", StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Verbose($"[RunPodServerless] PreGenerateEvent: explicit BackendType='{currentType}', skipping RunPod routing");
                    return; // Respect explicit backend type
                }
                // Determine requested model name
                string requestedModel = null;
                object m = p.UserInput.Get(T2IParamTypes.Model);
                if (m is T2IModel tm) { requestedModel = tm.Name; }
                else if (m is string ms) { requestedModel = ms; }
                if (string.IsNullOrWhiteSpace(requestedModel))
                {
                    Logs.Verbose($"[RunPodServerless] PreGenerateEvent: no model requested, skipping RunPod routing");
                    return;
                }
                Logs.Verbose($"[RunPodServerless] PreGenerateEvent: checking if model '{requestedModel}' is available on any RunPod backend...");
                // Check if any RunPod backend reports this model (in any subtype) via RemoteModels
                foreach (var b in Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>())
                {
                    var rem = b.RemoteModels;
                    if (rem is null)
                    {
                        Logs.Verbose($"[RunPodServerless] PreGenerateEvent: backend #{b.BackendData?.ID} has no RemoteModels, skipping");
                        continue;
                    }
                    bool found = rem.Values.Any(dict => dict.ContainsKey(requestedModel)
                        || dict.ContainsKey(requestedModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? requestedModel[..^".safetensors".Length] : requestedModel)
                        || dict.Keys.Any(k => k.Equals(requestedModel.AfterLast('/'), StringComparison.OrdinalIgnoreCase))
                        || dict.Keys.Any(k => k.Equals((requestedModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? requestedModel[..^".safetensors".Length] : requestedModel).AfterLast('/'), StringComparison.OrdinalIgnoreCase)));
                    if (found)
                    {
                        Logs.Verbose($"[RunPodServerless] PreGenerateEvent: model '{requestedModel}' found on backend #{b.BackendData?.ID}, routing to runpod_serverless");
                        p.UserInput.Set(T2IParamTypes.BackendType, "runpod_serverless");
                        return;
                    }
                    else
                    {
                        Logs.Verbose($"[RunPodServerless] PreGenerateEvent: model '{requestedModel}' not found on backend #{b.BackendData?.ID} (has {rem.Values.Sum(d => d.Count)} remote models)");
                    }
                }
                Logs.Verbose($"[RunPodServerless] PreGenerateEvent: model '{requestedModel}' not found on any RunPod backend, leaving BackendType as Any");
                // Otherwise: leave BackendType as Any so other backends can match
            };
            Logs.Debug("Registered PreGenerateEvent to default Backend Type to RunPod only when the requested model is available on RunPod.");
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to register scoped PreGenerateEvent handler for BackendType defaulting: {ex.Message}");
        }
        RunPodWebAPI.Register();
        Logs.Info("RunPod Serverless Backend extension loaded successfully.");
    }
}
