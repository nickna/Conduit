namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// Records whether the initial schema wait has completed. Starts current only in
    /// explicit Skip mode and is otherwise flipped by <see cref="MigrationWaitService"/>.
    /// Live readiness independently probes the schema on every request.
    /// </summary>
    public sealed class MigrationReadinessState
    {
        private volatile bool _isSchemaCurrent;

        public MigrationReadinessState(MigrationStartupOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _isSchemaCurrent = options.Mode == MigrationMode.Skip;
        }

        public bool IsSchemaCurrent
        {
            get => _isSchemaCurrent;
            set => _isSchemaCurrent = value;
        }
    }
}
