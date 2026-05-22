// Disable cross-class test parallelization. The integration tests use
// WebApplicationFactory<Program>, and Program.cs reads/writes a single
// config.json next to AppContext.BaseDirectory. When two test fixtures
// race on the same config.json.tmp atomic-rename window, File.Replace
// in SoundLibrary.SaveLocked can hang waiting for the file lock.
//
// Disabling parallelization keeps the entire run serial and removes
// the race without needing to thread a per-fixture root dir through
// Program.cs.

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
