using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Momo.Core.Entities;

namespace Momo.Infrastructure.Collab;

public class CollabClient
{
    private readonly HubConnection _connection;
    
    public string ConnectionId => _connection.ConnectionId ?? string.Empty;

    public event Action<OtOperation>? OnOperationReceived;
    public event Action<OtOperation>? OnOperationConfirmed;
    public event Action<string, int, int>? OnCursorReceived;
    public event Action<string>? OnUserJoined;
    public event Action<Comment>? OnCommentReceived;
    public event Action<MomoTask>? OnTaskUpdateReceived;
    public event Action<string>? OnRollbackApplied;

    public CollabClient(string serverUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .WithAutomaticReconnect()
            .Build();

        // Subscribe to server-side broadcasts
        _connection.On<OtOperation>("ReceiveOperation", op => OnOperationReceived?.Invoke(op));
        _connection.On<OtOperation>("ConfirmOperation", op => OnOperationConfirmed?.Invoke(op));
        _connection.On<string, int, int>("ReceiveCursor", (userId, paragraphIndex, charOffset) => 
            OnCursorReceived?.Invoke(userId, paragraphIndex, charOffset));
        _connection.On<string>("UserJoined", userId => OnUserJoined?.Invoke(userId));
        _connection.On<Comment>("comment_added", comment => OnCommentReceived?.Invoke(comment));
        _connection.On<MomoTask>("task_updated", task => OnTaskUpdateReceived?.Invoke(task));
        _connection.On<string>("rollback_applied", revisionId => OnRollbackApplied?.Invoke(revisionId));
    }

    public async Task StartAsync()
    {
        await _connection.StartAsync();
    }

    public async Task JoinGroupAsync(string transcriptId, string userId)
    {
        await _connection.SendAsync("JoinTranscriptGroup", transcriptId, userId);
    }

    public async Task SubmitOperationAsync(string transcriptId, OtOperation op)
    {
        await _connection.SendAsync("SendOperation", transcriptId, op);
    }

    public async Task SubmitCursorAsync(string transcriptId, string userId, int paragraphIndex, int charOffset)
    {
        await _connection.SendAsync("SendCursor", transcriptId, userId, paragraphIndex, charOffset);
    }

    public async Task SubmitCommentAsync(string transcriptId, Comment comment)
    {
        await _connection.SendAsync("SendComment", transcriptId, comment);
    }

    public async Task SubmitTaskUpdateAsync(string transcriptId, MomoTask task)
    {
        await _connection.SendAsync("SendTaskUpdate", transcriptId, task);
    }

    public async Task SubmitRollbackAsync(string transcriptId, string revisionId)
    {
        await _connection.SendAsync("ApplyRollback", transcriptId, revisionId);
    }

    public async Task StopAsync()
    {
        await _connection.StopAsync();
    }
}
