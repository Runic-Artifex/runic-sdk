- **Tests stop a process tree before deleting the directories it used.** `tests/support/TestProcesses.cs` ends a test's
  process with its descendants, waits for all of them on Linux, and deletes a directory on Windows with a bounded,
  scaled retry while a stopped process's handles close (#1268). The tool tests' workspace, the running-instance tests
  and the CS-WebUI browser guard checks use it.
