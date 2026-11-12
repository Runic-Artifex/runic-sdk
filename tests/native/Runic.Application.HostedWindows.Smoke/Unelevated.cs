using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// GitHub's Windows runners run every step elevated, and an elevated guest refuses hosted mode (host-protocol.md, Same
// user). The smoke, and the host cases it runs with --run-unelevated, therefore run as a standard user that the CI step
// creates (RUNIC_TEST_UNELEVATED_USER and RUNIC_TEST_UNELEVATED_PASSWORD), through CreateProcessWithLogonW, which also
// lets the user's process use this window station and desktop. Neither a restricted token of the elevated one (it keeps
// TokenIsElevated) nor the token of another process of the runner's user (none runs unelevated) would do. The child gets
// the user's own profile variables, such as TEMP, with this process's others, such as PATH, and RUNIC_TEST_UNELEVATED=1;
// it shares this process's standard handles and current directory, and its exit code is this process's.
[SupportedOSPlatform("windows")]
internal static partial class Unelevated
{
    internal const string Marker = "RUNIC_TEST_UNELEVATED";
    private const string UserVariable = "RUNIC_TEST_UNELEVATED_USER";
    private const string PasswordVariable = "RUNIC_TEST_UNELEVATED_PASSWORD";

    // The variables of the user's own profile, which the child keeps from the user's environment block.
    private static readonly HashSet<string> ProfileVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "USERNAME", "USERDOMAIN",
        "USERDOMAIN_ROAMINGPROFILE", "OneDrive", "ProgramData", "PUBLIC",
    };

    internal static int Run(IReadOnlyList<string> command)
    {
        if (command.Count == 0) throw new ArgumentException("No command to run unelevated.", nameof(command));
        if (Environment.GetEnvironmentVariable(Marker) == "1")
            throw new InvalidOperationException("This process was started unelevated and still runs elevated.");
        var user = Environment.GetEnvironmentVariable(UserVariable);
        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"Set {UserVariable} and {PasswordVariable} to a standard user to run unelevated.");
        Console.WriteLine($"Running as the standard user {user}: {string.Join(' ', command)}");
        Console.Out.Flush();
        Console.Error.Flush();
        var environment = EnvironmentBlock(UserEnvironment(user, password));
        var code = Start(user, password, environment, command);
        Console.WriteLine($"The unelevated command exited with {code}.");
        return code;
    }

    // The user's environment block (CreateEnvironmentBlock) after its profile was loaded, which creates it on the first
    // logon.
    private static unsafe Dictionary<string, string> UserEnvironment(string user, string password)
    {
        if (!LogonUserW(user, ".", password, 2 /* LOGON32_LOGON_INTERACTIVE */, 0, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                $"The user {user} could not log on (LOGON32_LOGON_INTERACTIVE, a password of {password.Length} characters).");
        try
        {
            fixed (char* name = user)
            {
                var profile = new ProfileInformation { Size = (uint)sizeof(ProfileInformation), Flags = 1 /* PI_NOUI */, UserName = name };
                if (!LoadUserProfileW(token, ref profile))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"The profile of {user} could not be loaded.");
                try
                {
                    if (!CreateEnvironmentBlock(out var block, token, false))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"The environment of {user} could not be read.");
                    try
                    {
                        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (var entry = (char*)block; *entry != '\0';)
                        {
                            var text = new string(entry);
                            entry += text.Length + 1;
                            var separator = text.IndexOf('=', 1);
                            if (separator > 0) variables[text[..separator]] = text[(separator + 1)..];
                        }
                        return variables;
                    }
                    finally { DestroyEnvironmentBlock(block); }
                }
                finally { _ = UnloadUserProfile(token, profile.Profile); }
            }
        }
        finally { CloseHandle(token); }
    }

    private static string EnvironmentBlock(Dictionary<string, string> user)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string name || variable.Value is not string value) continue;
            if (ProfileVariables.Contains(name) || name is UserVariable or PasswordVariable) continue;
            // The runner's file commands are the elevated step's; the user cannot write them, as a test platform's step
            // summary would.
            if (name.ToUpperInvariant() is "GITHUB_STEP_SUMMARY" or "GITHUB_OUTPUT" or "GITHUB_ENV" or "GITHUB_PATH" or "GITHUB_STATE") continue;
            variables[name] = value;
        }
        foreach (var (name, value) in user)
            if (ProfileVariables.Contains(name)) variables[name] = value;
        variables[Marker] = "1";
        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static unsafe int Start(string user, string password, string environment, IReadOnlyList<string> command)
    {
        var process = GetCurrentProcess();
        var standard = new nint[3];
        try
        {
            int[] ids = [-10, -11, -12];
            for (var index = 0; index < 3; index++)
            {
                var handle = GetStdHandle(ids[index]);
                if (handle is 0 or -1) continue;
                if (!DuplicateHandle(process, handle, process, out standard[index], 0, true, 2 /* DUPLICATE_SAME_ACCESS */))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "A standard handle could not be shared.");
            }
            var startup = new StartupInformation
            {
                Size = (uint)sizeof(StartupInformation),
                Flags = 0x100, // STARTF_USESTDHANDLES
                StandardInput = standard[0],
                StandardOutput = standard[1],
                StandardError = standard[2],
            };
            var line = (CommandLine(command) + "\0").ToCharArray();
            var directory = Environment.CurrentDirectory;
            ProcessInformation information;
            fixed (char* userName = user)
            fixed (char* domain = ".")
            fixed (char* secret = password)
            fixed (char* commandLine = line)
            fixed (char* environmentBlock = environment)
            fixed (char* currentDirectory = directory)
            {
                // LOGON_WITH_PROFILE; CREATE_UNICODE_ENVIRONMENT.
                if (!CreateProcessWithLogonW(userName, domain, secret, 1, null, commandLine, 0x400, environmentBlock, currentDirectory, &startup, out information))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"'{command[0]}' could not be started as {user}.");
            }
            CloseHandle(information.Thread);
            try
            {
                _ = WaitForSingleObject(information.Process, uint.MaxValue);
                return GetExitCodeProcess(information.Process, out var code) ? (int)code : 1;
            }
            finally { CloseHandle(information.Process); }
        }
        finally
        {
            foreach (var handle in standard)
                if (handle != 0) CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProfileInformation
    {
        public uint Size;
        public uint Flags;
        public char* UserName;
        public char* ProfilePath;
        public char* DefaultPath;
        public char* ServerName;
        public char* PolicyPath;
        public nint Profile;
    }

    // The quoting CommandLineToArgvW and the C runtime read back.
    private static string CommandLine(IReadOnlyList<string> command)
    {
        var line = new StringBuilder();
        foreach (var argument in command)
        {
            if (line.Length > 0) line.Append(' ');
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }
            line.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                line.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                line.Append(character);
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
        return line.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("advapi32", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUserW(string user, string domain, string password, int logonType, int provider, out nint token);

    [LibraryImport("userenv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LoadUserProfileW(nint token, ref ProfileInformation profile);

    [LibraryImport("userenv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnloadUserProfile(nint token, nint profile);

    [LibraryImport("userenv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessWithLogonW(char* userName, char* domain, char* password, uint logonFlags,
        char* applicationName, char* commandLine, uint creationFlags, void* environment, char* currentDirectory,
        StartupInformation* startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32")]
    private static partial nint GetStdHandle(int id);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
