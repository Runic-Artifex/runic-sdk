using TUnit.Core;

// The allocation budgets count the bytes allocated on the test thread while a case runs, and every case writes
// artifacts/scaling/results.json: run the cases one at a time (tests/scaling/README.md).
[assembly: NotInParallel]
