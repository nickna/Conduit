namespace ConduitLLM.Tests.Admin;

// These tests mutate the process-wide master key read by the Admin authentication pipeline.
// Run them serially and separately from other collections that also change the environment.
[CollectionDefinition("AdminMasterKeyEnvironment", DisableParallelization = true)]
public sealed class AdminMasterKeyEnvironmentCollection;
