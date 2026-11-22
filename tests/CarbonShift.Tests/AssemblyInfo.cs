// Desktop sessions share the process-wide active database connection. Do not switch it concurrently.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
