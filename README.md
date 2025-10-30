# RunPod Serverless Backend (Scaffold)

SwarmUI extension scaffold for a future RunPod serverless backend.

- Entry point: `RunPodServerlessExtension.cs` (extends `Extension`).
- Backend Type: `RunPodServerlessBackend` (extends `AbstractT2IBackend`) with a nested `Settings : AutoConfiguration` for Backends UI.
- No `.csproj`.
- Stubs only; no functionality is implemented yet.

Integration plan (high level):
- Register backend in `OnInit()` using `Program.Backends.RegisterBackendType`.
- Add optional `UserUpstreamApiKeys` registration to manage API keys in standard UI.
- Create a `RunPodWebAPI` class to register /API/RunPod/ routes (e.g., RefreshModels) similar to ComfyUIWebAPI.
- Use S3ModelDiscovery to find models without GPU and register them in `Program.MainSDModels` (see APIBackends pattern).
- In Generate(), use ParameterMapper and RunPodApiClient to call the RunPod serverless endpoint, then wrap results into Swarm `Image[]`.

Build by running SwarmUI update or launch-dev.
