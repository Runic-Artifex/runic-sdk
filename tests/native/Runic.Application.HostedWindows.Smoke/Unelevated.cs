using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// GitHub's Windows runners run every step elevated, and an elevated guest refuses hosted mode (host-protocol.md, Same
// user). The smoke, and the host cases it runs with --run-unelevated, therefore run as the runner's user without the
// elevation: with the token of an unelevated process of the same user in the same session, such as explorer.exe, which
// UAC's filtered token gives (CreateProcessWithTokenW). A restricted token of the elevated one would not do: it keeps
// the token's elevation (TokenIsElevated). The child shares this process's standard handles, gets its environment with
// RUNIC_TEST_UNELEVATED=1 and its current directory, and its exit code is this process's.
[SupportedOSPlatform("windows")]
internal static partial class Unelevated
{
    internal const string Marker = "RUNIC_TEST_UNELEVATED";

    internal static int Run(IReadOnlyList<string> command)
    {
        if (command.Count == 0) throw new ArgumentException("No command to run unelevated.", nameof(command));
        if (Environment.GetEnvironmentVariable(Marker) == "1")
            throw new InvalidOperationException("This process was started unelevated and still runs elevated.");
        Console.Out.Flush();
        Console.Error.Flush();
        var (token, source) = UnelevatedToken();
        try
        {
            Console.WriteLine($"Running unelevated with the token of {source}: {string.Join(' ', command)}");
            var code = Start(token, command);
            Console.WriteLine($"The unelevated command exited with {code}.");
            return code;
        }
        finally { CloseHandle(token); }
    }

    // A primary token of an unelevated, medium-integrity process of this user in this session.
    private static unsafe (nint Token, string Source) UnelevatedToken()
    {
        var self = OwnUser();
        var session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var candidates = System.Diagnostics.Process.GetProcesses()
            .OrderBy(static process => process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.SessionId != session || candidate.Id == Environment.ProcessId) continue;
                var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)candidate.Id);
                if (process == 0) continue;
                try
                {
                    if (!OpenProcessToken(process, 0x0002 | 0x0008 | 0x0001 /* DUPLICATE, QUERY, ASSIGN_PRIMARY */, out var token)) continue;
                    try
                    {
                        if (Integer(token, 20 /* TokenElevation */) != 0) continue;
                        if (IntegrityLevel(token) is not (>= 0x2000 and < 0x3000)) continue;
                        if (!User(token).AsSpan().SequenceEqual(self)) continue;
                        if (!DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var primary))
                            continue;
                        return (primary, $"{candidate.ProcessName} ({candidate.Id})");
                    }
                    finally { CloseHandle(token); }
                }
                finally { CloseHandle(process); }
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        throw new InvalidOperationException("No unelevated process of this user runs in this session, so nothing can be started unelevated.");
    }

    private static byte[] OwnUser()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "This process's token could not be read.");
        try { return User(token); }
        finally { CloseHandle(token); }
    }

    private static unsafe byte[] Information(nint token, int informationClass)
    {
        _ = GetTokenInformation(token, informationClass, null, 0, out var size);
        if (size == 0) return [];
        var buffer = new byte[size];
        fixed (byte* data = buffer)
            return GetTokenInformation(token, informationClass, data, size, out _) ? buffer : [];
    }

    private static int Integer(nint token, int informationClass) =>
        Information(token, informationClass) is { Length: >= 4 } data ? BitConverter.ToInt32(data) : -1;

    // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }.
    private static unsafe byte[] User(nint token)
    {
        var data = Information(token, 1 /* TokenUser */);
        if (data.Length < sizeof(nint)) return [];
        fixed (byte* pointer = data)
        {
            var sid = *(nint*)pointer;
            return new ReadOnlySpan<byte>((void*)sid, GetLengthSid(sid)).ToArray();
        }
    }

    // The RID of the token's integrity SID: 0x2000 medium, 0x3000 high.
    private static unsafe int IntegrityLevel(nint token)
    {
        var data = Information(token, 25 /* TokenIntegrityLevel */);
        if (data.Length < sizeof(nint)) return -1;
        fixed (byte* pointer = data)
        {
            var sid = *(nint*)pointer;
            var count = *GetSidSubAuthorityCount(sid);
            return count == 0 ? -1 : (int)*GetSidSubAuthority(sid, (uint)(count - 1));
        }
    }

    private static string EnvironmentBlock()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (variable.Key is string name && variable.Value is string value) variables[name] = value;
        variables[Marker] = "1";
        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static unsafe int Start(nint token, IReadOnlyList<string> command)
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
            var environment = EnvironmentBlock();
            var directory = Environment.CurrentDirectory;
            ProcessInformation information;
            fixed (char* commandLine = line)
            fixed (char* environmentBlock = environment)
            fixed (char* currentDirectory = directory)
            {
                // CREATE_UNICODE_ENVIRONMENT. The secondary logon service duplicates the standard handles.
                if (!CreateProcessWithTokenW(token, 0, null, commandLine, 0x400, environmentBlock, currentDirectory, &startup, out information))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"'{command[0]}' could not be started unelevated.");
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

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returned);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("advapi32")]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("advapi32")]
    private static unsafe partial byte* GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32")]
    private static unsafe partial uint* GetSidSubAuthority(nint sid, uint index);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessWithTokenW(nint token, uint logonFlags, char* applicationName, char* commandLine,
        uint creationFlags, void* environment, char* currentDirectory, StartupInformation* startupInfo, out ProcessInformation processInformation);

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
