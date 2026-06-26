using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Momo.Infrastructure.Collab;

public class CollabHub : Hub
{
    private static readonly Dictionary<string, List<OtOperation>> _histories = new();
    private static readonly object _historyLock = new();

    private List<OtOperation> GetHistory(string transcriptId)
    {
        lock (_historyLock)
        {
            if (!_histories.TryGetValue(transcriptId, out var history))
            {
                history = new List<OtOperation>();
                _histories[transcriptId] = history;
            }
            return history;
        }
    }

    public async Task JoinTranscriptGroup(string transcriptId, string userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, transcriptId);
        
        // Notify others about user presence
        await Clients.OthersInGroup(transcriptId).SendAsync("UserJoined", userId);
    }

    public async Task SendOperation(string transcriptId, OtOperation op)
    {
        List<OtOperation> history = GetHistory(transcriptId);
        
        lock (_historyLock)
        {
            int clientRevision = op.Revision;
            
            // Transform op against all concurrent operations in the history
            for (int i = clientRevision; i < history.Count; i++)
            {
                var historicalOp = history[i];
                var (transformedClientOp, _) = OtEngine.Transform(op, historicalOp);
                op = transformedClientOp;
            }
            
            op.Revision = history.Count;
            history.Add(op);
        }

        // Broadcast the transformed operation to others in the group
        await Clients.OthersInGroup(transcriptId).SendAsync("ReceiveOperation", op);
        
        // Send confirmation back to the sender with the final committed revision
        await Clients.Caller.SendAsync("ConfirmOperation", op);
    }

    public async Task SendCursor(string transcriptId, string userId, int paragraphIndex, int charOffset)
    {
        await Clients.OthersInGroup(transcriptId).SendAsync("ReceiveCursor", userId, paragraphIndex, charOffset);
    }
}
