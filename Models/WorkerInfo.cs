namespace Hartsy.Extensions.RunPodServerless.Models;

/// <summary>Worker information returned from wakeup or ready check.</summary>
public class WorkerInfo
{
    public string PublicUrl { get; set; }
    public string SessionId { get; set; }
    public string WorkerId { get; set; }
    public string Version { get; set; }
}

/// <summary>Worker ready check response.</summary>
public class WorkerReadyResponse
{
    public bool Ready { get; set; }
    public string PublicUrl { get; set; }
    public string SessionId { get; set; }
    public string WorkerId { get; set; }
    public string Version { get; set; }
    public string Error { get; set; }
}
