// Derived from the Command Palette extension template.
// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace Recall.CmdPal;

public class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
        {
            Console.WriteLine("Not launched as a Command Palette extension. Exiting.");
            return;
        }

        ComServer server = new();
        ManualResetEvent extensionDisposed = new(false);
        // One extension instance for the whole process: the host always receives the same object.
        RecallExtension extension = new(extensionDisposed);
        server.RegisterClass<RecallExtension, IExtension>(() => extension);
        server.Start();
        extensionDisposed.WaitOne();
        server.Stop();
        server.UnsafeDispose();
    }
}
