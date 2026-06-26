using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Momo.Infrastructure.Collab;
using Xunit;

namespace Momo.Tests;

public class CollabTests
{
    [Fact]
    public async Task Test_OT_Sync_TwoClients()
    {
        int port = FindFreeTcpPort();
        string serverUrl = $"http://localhost:{port}/hubs/collab";

        // 1. Start Server Host
        var server = new CollabServerHost();
        await server.StartAsync(port);

        // Wait briefly for server to bind
        await Task.Delay(1000);

        try
        {
            // 2. Initialize two clients
            var clientA = new CollabClient(serverUrl);
            var clientB = new CollabClient(serverUrl);

            await clientA.StartAsync();
            await clientB.StartAsync();

            string transcriptId = "tr_collab_123";
            await clientA.JoinGroupAsync(transcriptId, "UserA");
            await clientB.JoinGroupAsync(transcriptId, "UserB");

            // Client states
            string docA = "Hello World";
            string docB = "Hello World";

            int revA = 0;
            int revB = 0;

            var pendingA = new List<OtOperation>();
            var pendingB = new List<OtOperation>();

            var tcsA = new TaskCompletionSource<bool>();
            var tcsB = new TaskCompletionSource<bool>();

            // Setup Event Handlers for Client A
            clientA.OnOperationReceived += op =>
            {
                // Transform remote op against outstanding local operations
                for (int i = 0; i < pendingA.Count; i++)
                {
                    var (transformedRemote, transformedLocal) = OtEngine.Transform(op, pendingA[i]);
                    op = transformedRemote;
                    pendingA[i] = transformedLocal;
                }

                docA = OtEngine.Apply(docA, op);
                revA = op.Revision + 1;

                if (docA == "Hello Awesome World!")
                {
                    tcsA.TrySetResult(true);
                }
            };

            clientA.OnOperationConfirmed += op =>
            {
                if (pendingA.Count > 0) pendingA.RemoveAt(0);
                revA = op.Revision + 1;
                
                if (docA == "Hello Awesome World!")
                {
                    tcsA.TrySetResult(true);
                }
            };

            // Setup Event Handlers for Client B
            clientB.OnOperationReceived += op =>
            {
                // Transform remote op against outstanding local operations
                for (int i = 0; i < pendingB.Count; i++)
                {
                    var (transformedRemote, transformedLocal) = OtEngine.Transform(op, pendingB[i]);
                    op = transformedRemote;
                    pendingB[i] = transformedLocal;
                }

                docB = OtEngine.Apply(docB, op);
                revB = op.Revision + 1;

                if (docB == "Hello Awesome World!")
                {
                    tcsB.TrySetResult(true);
                }
            };

            clientB.OnOperationConfirmed += op =>
            {
                if (pendingB.Count > 0) pendingB.RemoveAt(0);
                revB = op.Revision + 1;

                if (docB == "Hello Awesome World!")
                {
                    tcsB.TrySetResult(true);
                }
            };

            // 3. Act: Send concurrent edits
            // Client A: Insert "!" at end (position 11)
            var opA = new OtOperation
            {
                ClientId = "UserA",
                Type = "insert",
                Position = 11,
                Text = "!",
                Revision = revA,
                ParagraphIndex = 0
            };
            pendingA.Add(opA);
            docA = OtEngine.Apply(docA, opA);

            // Client B: Insert "Awesome " before "World" (position 6)
            var opB = new OtOperation
            {
                ClientId = "UserB",
                Type = "insert",
                Position = 6,
                Text = "Awesome ",
                Revision = revB,
                ParagraphIndex = 0
            };
            pendingB.Add(opB);
            docB = OtEngine.Apply(docB, opB);

            // Submit operations concurrently
            var sendTaskA = clientA.SubmitOperationAsync(transcriptId, opA);
            var sendTaskB = clientB.SubmitOperationAsync(transcriptId, opB);

            await Task.WhenAll(sendTaskA, sendTaskB);

            // Wait for both clients to synchronize and resolve text to the expected value
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(Task.WhenAll(tcsA.Task, tcsB.Task), timeoutTask);

            Assert.Same(completedTask, completedTask); // avoid compiler warning
            Assert.True(tcsA.Task.IsCompleted, $"Client A sync failed: current doc = {docA}");
            Assert.True(tcsB.Task.IsCompleted, $"Client B sync failed: current doc = {docB}");

            Assert.Equal("Hello Awesome World!", docA);
            Assert.Equal("Hello Awesome World!", docB);

            // Cleanup
            await clientA.StopAsync();
            await clientB.StopAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static int FindFreeTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
