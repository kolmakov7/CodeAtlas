// Some tests (e.g. ApiIntegrationTests) mutate process-global state such as the
// CODERAG__DataDir environment variable, which LocalPath.ReposRoot reads dynamically.
// Running collections in parallel would let that mutation bleed into unrelated tests,
// so we disable parallelization to keep test isolation deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
