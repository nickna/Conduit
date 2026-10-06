using System.ComponentModel.DataAnnotations;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Records a single execution of the provider metadata sync (fetch + drift detection).
    /// </summary>
    public class ProviderMetadataSyncRun : IEntity<int>
    {
        /// <summary>Unique identifier for the sync run.</summary>
        [Key]
        public int Id { get; set; }

        /// <summary>The provider type this run covered (currently only OpenRouter).</summary>
        public ProviderType ProviderType { get; set; } = ProviderType.OpenRouter;

        /// <summary>When the run started (UTC).</summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the run completed (UTC), or null if still running.</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>Run status: Running | Completed | Failed | Cancelled.</summary>
        [StringLength(20)]
        public string Status { get; set; } = "Running";

        /// <summary>What triggered the run: "Schedule" or "Manual".</summary>
        [StringLength(100)]
        public string TriggeredBy { get; set; } = "Schedule";

        /// <summary>Number of models fetched from the provider catalog.</summary>
        public int ModelsFetched { get; set; }

        /// <summary>Number of model mappings checked for drift.</summary>
        public int MappingsChecked { get; set; }

        /// <summary>Number of new drift items created this run.</summary>
        public int ItemsCreated { get; set; }

        /// <summary>Number of existing pending drift items refreshed this run.</summary>
        public int ItemsUpdated { get; set; }

        /// <summary>Number of pending drift items auto-resolved (no longer drifting) this run.</summary>
        public int ItemsAutoResolved { get; set; }

        /// <summary>Error message when the run failed.</summary>
        [StringLength(2000)]
        public string? ErrorMessage { get; set; }
    }
}
