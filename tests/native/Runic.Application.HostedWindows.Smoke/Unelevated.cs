using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// GitHub's Windows runners run every step elevated, and an elevated guest refuses hosted mode (host-protocol.md, Same
// user). The smoke, and the host cases it runs with --run-unelevated, therefore run with the runner user's token as a
// standard user has it: SaferComputeTokenFromLevel(SAFER_LEVELID_NORMALUSER) keeps the user's SID, makes Administrators
// deny-only and drops the privileges, and the token is lowered to medium integrity. The child shares this process's
// standard handles and its exit code is this process's.
[SupportedOSPlatform("windows")]
internal static partial class Unelevated
{
    internal static int Run(IReadOnlyList<string> command)
    {
        if (command.Count == 0) throw new ArgumentException("No command to run unelevated.", nameof(command));
        Console.Out.Flush();
        Console.Error.Flush();
        if (!SaferCreateLevel(2 /* SAFER_SCOPEID_USER */, 0x20000 /* SAFER_LEVELID_NORMALUSER */, 1 /* SAFER_LEVEL_OPEN */, out var level, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SaferCreateLevel failed.");
        nint token = 0;
        try
        {
            if (!SaferComputeTokenFromLevel(level, 0, out token, 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SaferComputeTokenFromLevel failed.");
            LowerToMediumIntegrity(token);
            var code = Start(token, command);
            Console.WriteLine($"The unelevated command exited with {code}.");
            return code;
        }
        finally
        {
            if (token != 0) CloseHandle(token);
            SaferCloseLevel(level);
        }
    }

    private static unsafe void LowerToMediumIntegrity(nint token)
    {
        if (!ConvertStringSidToSidW("S-1-16-8192", out var sid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The medium integrity SID could not be created.");
        try
        {
            // TOKEN_MANDATORY_LABEL: SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes = SE_GROUP_INTEGRITY; }.
            var label = stackalloc nint[2];
            label[0] = sid;
            label[1] = 0x20;
            if (!SetTokenInformation(token, 25 /* TokenIntegrityLevel */, label, (uint)(2 * sizeof(nint)) + (uint)GetLengthSid(sid)))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The token's integrity level could not be lowered.");
        }
        finally { LocalFree(sid); }
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
            ProcessInformation information;
            fixed (char* commandLine = line)
            {
                if (!CreateProcessAsUserW(token, null, commandLine, 0, 0, true, 0, null, null, &startup, out information))
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

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCreateLevel(uint scope, uint level, uint openFlags, out nint handle, nint reserved);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferComputeTokenFromLevel(nint level, nint inputToken, out nint outputToken, uint flags, nint reserved);

    [LibraryImport("advapi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCloseLevel(nint level);

    [LibraryImport("advapi32", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSidW(string text, out nint sid);

    [LibraryImport("advapi32")]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetTokenInformation(nint token, int informationClass, void* information, uint length);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUserW(nint token, char* applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, void* environment,
        char* currentDirectory, StartupInformation* startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32")]
    private static partial nint LocalFree(nint memory);

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
