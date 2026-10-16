using TUnit.Core;

// The pkexec cases write executable scripts and then start them. A process another case forks while a script is
// still open for writing inherits that descriptor until it execs, and starting the script in that window fails with
// ETXTBSY, which the provider reports as an unavailable pkexec (#298). Run the cases one at a time, so no case forks
// while another writes its scripts.
[assembly: NotInParallel]
