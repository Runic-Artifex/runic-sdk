using Runic.Platform.Administration.Windows;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Dns;
using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.GroupPolicy;
using Runic.Platform.Administration.Windows.Networks;
using Runic.Platform.Administration.Windows.Processes;
using Runic.Platform.Administration.Windows.Services;
using Runic.Platform.Administration.Windows.Shares;
using Runic.Platform.Administration.Windows.Shortcuts;
using Runic.Platform.Administration.Windows.SystemInformation;
using Runic.Platform.Administration.Windows.Tasks;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These examples require Windows.");
if (args.Length == 0)
{
    Console.WriteLine("Commands: system | processes | services | networks | shares [server] | tasks | firewall | shortcut <absolute.lnk> | domain | ldap <controller> | dns <server> | gpo <domain> <controller>");
    return;
}
try
{
    switch (args[0])
    {
        case "system":
            var system = new WindowsSystemInformationClient();
            Console.WriteLine(await system.GetOperatingSystemNameAsync());
            Console.WriteLine(system.GetOperatingSystem());
            foreach (var bios in system.GetBiosInformation()) Console.WriteLine(bios);
            break;
        case "processes":
            foreach (var process in new WindowsProcessClient().Enumerate()) Console.WriteLine(process);
            break;
        case "services":
            Console.WriteLine(new WindowsServiceClient().Find("EventLog"));
            break;
        case "networks":
            foreach (var network in await new WindowsNetworkClient().EnumerateAsync()) Console.WriteLine(network);
            break;
        case "shares":
            foreach (var share in new WindowsShareClient(args.Length > 1 ? args[1] : null).Enumerate()) Console.WriteLine(share);
            break;
        case "tasks":
            foreach (var task in await new WindowsTaskSchedulerClient().EnumerateAsync("\\")) Console.WriteLine($"{task.Path}: {task.State}; last result {task.LastTaskResult}");
            break;
        case "firewall":
            foreach (var profile in await new WindowsFirewallClient().GetProfilesAsync()) Console.WriteLine(profile);
            break;
        case "shortcut" when args.Length == 2:
            Console.WriteLine(await new WindowsShellLinkClient().FindAsync(args[1]));
            break;
        case "domain":
            Console.WriteLine(new WindowsDomainClient().GetMembership());
            break;
        case "ldap" when args.Length == 2:
            var root = await new WindowsDirectoryClient(new(args[1])).ReadRootDseAsync(["defaultNamingContext", "dnsHostName"]);
            if (root is not null)
                foreach (var pair in root.Attributes) Console.WriteLine($"{pair.Key}: {string.Join(", ", pair.Value)}");
            break;
        case "dns" when args.Length == 2:
            foreach (var zone in await new WindowsDnsClient(args[1]).EnumerateZonesAsync()) Console.WriteLine(zone);
            break;
        case "gpo" when args.Length == 3:
            foreach (var gpo in await new WindowsGroupPolicyClient(args[1], args[2]).EnumerateAsync()) Console.WriteLine(gpo);
            break;
        default:
            throw new ArgumentException("Unknown command or missing explicit target. Run without arguments for usage.");
    }
}
catch (WindowsAdministrationException error)
{
    Console.Error.WriteLine($"{error.Operation}: {error.Category} ({error.NativeErrorDomain} 0x{error.NativeErrorCode:X8})");
    Environment.ExitCode = 1;
}
