using Xunit;

// Console.Out and Console.Error are process-wide and several acceptance tests
// temporarily replace them. Do not run those test classes concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
