// Prints a URL line as a serve-only Runic application does, serves nothing, and exits on a line on standard input:
// startup.mjs --url-only times it as the floor of a platform's process start.
System.Console.Out.WriteLine("RUNIC_APPLICATION_URL=http://127.0.0.1:0/");
System.Console.In.ReadLine();
