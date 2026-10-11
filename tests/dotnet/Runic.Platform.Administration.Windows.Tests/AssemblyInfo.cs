using TUnit.Core;

// The cases query live machine state (services, processes, firewall, Task Scheduler, WMI and GPMC through COM
// apartments) and create temporary shortcut and backup folders. Run them one at a time, as the former runner did.
[assembly: NotInParallel]
